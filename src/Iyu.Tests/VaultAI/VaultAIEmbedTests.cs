using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Iyu.Tests.Server.OData;
using Iyu.VaultAI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// <c>MapVaultAIEmbed()</c> — the server half of embedding the chat widget. Through the real TestServer pipeline, with the
/// chat service replaced by a fake handler so what is sent to it and what is let out are both observable.
/// </summary>
public sealed class VaultAIEmbedTests : IDisposable
{
    private const string Policy = "ChatUsers";
    private const string ServerKey = "server-key-123";
    private const string ConfigPath = "/api/vault-ai-embed/config";
    private const string TokenPath = "/api/vault-ai-embed/token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "iyu-vaultai-embed-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _apps = [];

    public VaultAIEmbedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var app in _apps) app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Upstream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string? Uri, string? Authorization)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((request.Method, request.RequestUri?.OriginalString, request.Headers.Authorization?.ToString()));
            return respond(request, cancellationToken);
        }
    }

    private static Upstream Answering(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private static Upstream Issuing(string body = """{"token":"short-lived-token","expiresAt":"2030-01-01T00:00:00Z"}""") =>
        Answering(HttpStatusCode.OK, body);

    private WebApplicationBuilder Builder(Upstream upstream, (string Key, string? Value)[]? vaultAI = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["VaultAI:Url"] = "https://vault.example.test",
            ["VaultAI:Token"] = ServerKey,
            ["VaultAI:ChannelId"] = "chan-1",
            ["VaultAI:ReportPath"] = _root,
        };
        foreach (var (key, value) in vaultAI ?? []) settings["VaultAI:" + key] = value;
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, p => p.RequireClaim("perm", Policy));
        builder.Services.AddVaultAIReports(builder.Configuration);
        // Registered after the module's own, so this handler is the one the token call ends up using.
        builder.Services.AddHttpClient(VaultAIEmbedExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => upstream);
        return builder;
    }

    private async Task<HttpClient> Host(Upstream upstream, (string Key, string? Value)[]? vaultAI = null, bool withReports = false)
    {
        var app = Builder(upstream, vaultAI).Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (withReports) app.UseVaultAIReports();
        app.MapVaultAIEmbed();
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestServer().CreateClient();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? perm = "anyone", string? site = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (perm is not null) req.Headers.Add("X-Test-Perm", perm);
        if (site is not null) req.Headers.Add("Sec-Fetch-Site", site);
        return req;
    }

    [Theory]
    [InlineData("GET", ConfigPath)]
    [InlineData("POST", TokenPath)]
    public async Task An_anonymous_caller_is_challenged_and_nothing_is_asked_of_the_chat_service(string method, string path)
    {
        var upstream = Issuing();
        var client = await Host(upstream);

        using var resp = await client.SendAsync(Request(new HttpMethod(method), path, perm: null));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task A_signed_in_user_is_served_when_no_policy_is_named()
    {
        var client = await Host(Issuing());

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath, perm: "anything"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("GET", ConfigPath)]
    [InlineData("POST", TokenPath)]
    public async Task A_named_policy_forbids_a_user_who_does_not_meet_it_and_admits_one_who_does(string method, string path)
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: [("AuthorizationPolicy", Policy)]);

        using var forbidden = await client.SendAsync(Request(new HttpMethod(method), path, perm: "other"));
        var callsAfterForbidden = upstream.Calls.Count;
        using var admitted = await client.SendAsync(Request(new HttpMethod(method), path, perm: Policy));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, callsAfterForbidden);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
    }

    [Fact]
    public async Task AllowAnonymous_is_the_only_way_to_open_it_to_an_unsigned_caller()
    {
        var client = await Host(Issuing(), vaultAI: [("AllowAnonymous", "true")]);

        using var config = await client.SendAsync(Request(HttpMethod.Get, ConfigPath, perm: null));
        using var token = await client.SendAsync(Request(HttpMethod.Post, TokenPath, perm: null));

        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
    }

    [Fact]
    public void A_policy_name_that_is_not_registered_fails_when_the_routes_are_mapped()
    {
        var app = Builder(Issuing(), [("AuthorizationPolicy", "NoSuchPolicy")]).Build();
        _apps.Add(app);

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapVaultAIEmbed());

        Assert.Contains("NoSuchPolicy", ex.Message);
    }

    [Fact]
    public async Task The_routes_coexist_with_the_report_module_under_the_same_policy()
    {
        var client = await Host(Issuing(), withReports: true, vaultAI: [("AuthorizationPolicy", Policy)]);

        using var anonymous = await client.SendAsync(Request(HttpMethod.Get, ConfigPath, perm: null));
        using var other = await client.SendAsync(Request(HttpMethod.Get, ConfigPath, perm: "other"));
        using var admitted = await client.SendAsync(Request(HttpMethod.Get, ConfigPath, perm: Policy));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
    }

    [Theory]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.OK)]
    [InlineData("none", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.OK)]
    public async Task A_token_request_another_site_started_is_refused_before_the_chat_service_is_asked(string? site, HttpStatusCode expected)
    {
        var upstream = Issuing();
        var client = await Host(upstream);

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath, site: site));

        Assert.Equal(expected, resp.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, upstream.Calls.Count);
    }

    [Fact]
    public async Task Config_names_where_to_load_the_widget_from_and_the_channel()
    {
        var client = await Host(Issuing(), vaultAI: [("Url", "https://vault.example.test/")]);

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ConfigPath));
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((bool)body["enabled"]!);
        Assert.Equal("https://vault.example.test", (string?)body["origin"]);
        Assert.Equal("chan-1", (string?)body["channelId"]);
    }

    [Fact]
    public async Task Config_gives_the_browser_the_embed_origin_when_the_server_address_is_not_the_browsers()
    {
        var client = await Host(Issuing(), vaultAI: [("Url", "http://10.0.0.5:8080"), ("EmbedOrigin", "https://chat.example.test/")]);

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ConfigPath));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal("https://chat.example.test", (string?)JsonNode.Parse(text)!["origin"]);
        Assert.DoesNotContain("10.0.0.5", text);
    }

    [Fact]
    public async Task Without_a_channel_config_says_not_enabled_and_token_says_not_configured()
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: [("ChannelId", null)]);

        using var config = await client.SendAsync(Request(HttpMethod.Get, ConfigPath));
        using var token = await client.SendAsync(Request(HttpMethod.Post, TokenPath));
        var body = JsonNode.Parse(await config.Content.ReadAsStringAsync())!;

        Assert.False((bool)body["enabled"]!);
        Assert.Equal("", (string?)body["origin"]);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, token.StatusCode);
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task With_no_chat_service_address_the_module_maps_no_routes_at_all()
    {
        var client = await Host(Issuing(), vaultAI: [("Url", null)]);

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ConfigPath));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_token_is_asked_for_with_the_servers_key_and_only_the_two_fields_it_needs_come_back()
    {
        var upstream = Issuing("""{"token":"short-lived-token","expiresAt":"2030-01-01T00:00:00Z","extra":"must-not-leak"}""");
        var client = await Host(upstream);

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath));
        var text = await resp.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(text)!.AsObject();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("short-lived-token", (string?)body["token"]);
        Assert.Equal("2030-01-01T00:00:00Z", (string?)body["expiresAt"]);
        Assert.Equal(2, body.Count);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(ServerKey, text);
        var call = Assert.Single(upstream.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://vault.example.test/api/channels/chan-1/token", call.Uri);
        Assert.Equal("Bearer " + ServerKey, call.Authorization);
    }

    [Fact]
    public async Task A_channel_id_is_escaped_into_the_path()
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: [("ChannelId", "a b/c")]);

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("https://vault.example.test/api/channels/a%20b%2Fc/token", Assert.Single(upstream.Calls).Uri);
    }

    [Fact]
    public async Task A_refusal_from_the_chat_service_is_a_502_that_repeats_nothing_of_its_answer()
    {
        var client = await Host(Answering(HttpStatusCode.InternalServerError, "UPSTREAM-DETAIL " + ServerKey));

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.DoesNotContain("UPSTREAM-DETAIL", text);
        Assert.DoesNotContain(ServerKey, text);
    }

    [Fact]
    public async Task A_chat_service_that_cannot_be_reached_is_a_502_that_repeats_nothing_of_the_failure()
    {
        var client = await Host(new Upstream((_, _) => throw new HttpRequestException("boom-detail")));

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.DoesNotContain("boom-detail", text);
    }

    [Fact]
    public async Task A_chat_service_that_is_slow_is_a_502_after_the_timeout()
    {
        var upstream = new Upstream(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = await Host(upstream, vaultAI: [("EmbedTokenTimeout", "00:00:00.100")]);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        // The answer must come from the module's own deadline, not from the HTTP client's default (a hundred seconds),
        // which would also end in a 502 but only after the page's widget request had hung that long.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Fact]
    public void The_token_client_leaves_the_deadline_to_the_module()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VaultAI:Url"] = "https://vault.example.test", ["VaultAI:Token"] = "t" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVaultAIReports(config);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(VaultAIEmbedExtensions.HttpClientName);

        // Otherwise the HTTP client's own hundred seconds would end a longer VaultAI:EmbedTokenTimeout early.
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Theory]
    [InlineData("""{"expiresAt":"2030-01-01T00:00:00Z"}""")]
    [InlineData("""{"token":""}""")]
    [InlineData("""{"token":42}""")]
    [InlineData("not json")]
    public async Task An_answer_without_a_token_is_a_502(string body)
    {
        var client = await Host(Issuing(body));

        using var resp = await client.SendAsync(Request(HttpMethod.Post, TokenPath));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    // ---- several channels: the allow list (VaultAI:EmbedChannels) ----

    private const string ChannelsPath = "/api/vault-ai-embed/channels";
    private static string ChannelTokenPath(string id) => $"{ChannelsPath}/{id}/token";

    private static readonly (string Key, string? Value)[] TwoChannels =
    [
        ("EmbedChannels:0:Id", "chan-a"), ("EmbedChannels:0:Label", "Support"),
        ("EmbedChannels:1:Id", "chan-b"),
    ];

    [Fact]
    public async Task The_channel_list_is_the_allow_list_with_the_names_the_picker_shows()
    {
        var client = await Host(Issuing(), vaultAI: TwoChannels);

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ChannelsPath));
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((bool)body["enabled"]!);
        Assert.Equal("https://vault.example.test", (string?)body["origin"]);
        var channels = body["channels"]!.AsArray();
        Assert.Equal(2, channels.Count);
        Assert.Equal("chan-a", (string?)channels[0]!["id"]);
        Assert.Equal("Support", (string?)channels[0]!["label"]);
        Assert.Equal("chan-b", (string?)channels[1]!["id"]);
        Assert.Null(channels[1]!["label"]);
    }

    [Fact]
    public async Task The_list_does_not_carry_the_single_channel_nor_the_servers_key()
    {
        var client = await Host(Issuing(), vaultAI: TwoChannels);   // ChannelId "chan-1" is also configured

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ChannelsPath));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("chan-1", text);
        Assert.DoesNotContain(ServerKey, text);
    }

    [Fact]
    public async Task Without_a_list_the_channel_routes_say_not_enabled_and_ask_nothing_of_the_chat_service()
    {
        var upstream = Issuing();
        var client = await Host(upstream);   // ChannelId is set, EmbedChannels is not

        using var list = await client.SendAsync(Request(HttpMethod.Get, ChannelsPath));
        using var token = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("chan-1")));
        var body = JsonNode.Parse(await list.Content.ReadAsStringAsync())!;

        Assert.False((bool)body["enabled"]!);
        Assert.Empty(body["channels"]!.AsArray());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, token.StatusCode);
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task A_token_for_a_listed_channel_is_asked_for_with_the_servers_key_and_only_the_two_fields_come_back()
    {
        var upstream = Issuing("""{"token":"short-lived-token","expiresAt":"2030-01-01T00:00:00Z","extra":"must-not-leak"}""");
        var client = await Host(upstream, vaultAI: TwoChannels);

        using var resp = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("chan-b")));
        var text = await resp.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(text)!.AsObject();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("short-lived-token", (string?)body["token"]);
        Assert.Equal(2, body.Count);
        Assert.True(resp.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(ServerKey, text);
        var call = Assert.Single(upstream.Calls);
        Assert.Equal("https://vault.example.test/api/channels/chan-b/token", call.Uri);
        Assert.Equal("Bearer " + ServerKey, call.Authorization);
    }

    [Theory]
    [InlineData("chan-1")]       // the single channel is not on the list: the list is the only way in
    [InlineData("CHAN-A")]       // ids are compared exactly
    [InlineData("chan-a%20")]    // padded
    [InlineData("nope")]
    [InlineData("chan-a%2Fchan-b")]
    public async Task A_channel_that_is_not_listed_is_a_404_and_the_chat_service_is_not_asked(string id)
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: TwoChannels);

        using var resp = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath(id)));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(upstream.Calls);
        Assert.DoesNotContain("chan-a", text);   // the answer does not name what is listed
    }

    [Fact]
    public async Task A_listed_id_with_characters_that_need_escaping_is_escaped_into_the_path()
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: [("EmbedChannels:0:Id", "a b?c")]);

        using var resp = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("a%20b%3Fc")));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("https://vault.example.test/api/channels/a%20b%3Fc/token", Assert.Single(upstream.Calls).Uri);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task A_channel_token_request_another_site_started_is_refused_before_anything_else(string site)
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: TwoChannels);

        using var listed = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("chan-a"), site: site));
        using var unlisted = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("nope"), site: site));

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlisted.StatusCode);   // not told whether it was listed
        Assert.Empty(upstream.Calls);
    }

    [Theory]
    [InlineData("GET", ChannelsPath)]
    [InlineData("POST", "/api/vault-ai-embed/channels/chan-a/token")]
    public async Task The_channel_routes_are_under_the_modules_authorization(string method, string path)
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: [.. TwoChannels, ("AuthorizationPolicy", Policy)]);

        using var anonymous = await client.SendAsync(Request(new HttpMethod(method), path, perm: null));
        using var other = await client.SendAsync(Request(new HttpMethod(method), path, perm: "other"));
        var callsAfterRefusals = upstream.Calls.Count;
        using var admitted = await client.SendAsync(Request(new HttpMethod(method), path, perm: Policy));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(0, callsAfterRefusals);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
    }

    [Fact]
    public async Task A_refusal_from_the_chat_service_for_a_listed_channel_is_a_502_that_repeats_nothing_of_its_answer()
    {
        var client = await Host(Answering(HttpStatusCode.InternalServerError, "UPSTREAM-DETAIL " + ServerKey), vaultAI: TwoChannels);

        using var resp = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("chan-a")));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.DoesNotContain("UPSTREAM-DETAIL", text);
        Assert.DoesNotContain(ServerKey, text);
    }

    [Fact]
    public async Task The_single_channel_routes_are_unchanged_by_a_list()
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: TwoChannels);

        using var config = await client.SendAsync(Request(HttpMethod.Get, ConfigPath));
        using var token = await client.SendAsync(Request(HttpMethod.Post, TokenPath));

        Assert.Equal("chan-1", (string?)JsonNode.Parse(await config.Content.ReadAsStringAsync())!["channelId"]);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.Equal("https://vault.example.test/api/channels/chan-1/token", Assert.Single(upstream.Calls).Uri);
    }

    [Theory]
    [InlineData("", "blank")]
    [InlineData("   ", "blank")]
    [InlineData(" chan-a", "whitespace")]
    [InlineData("chan-a ", "whitespace")]
    public void A_blank_or_padded_id_in_the_list_fails_when_the_routes_are_mapped(string id, string expected)
    {
        var app = Builder(Issuing(), [("EmbedChannels:0:Id", id)]).Build();
        _apps.Add(app);

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapVaultAIEmbed());

        Assert.Contains("EmbedChannels:0:Id", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void An_id_listed_twice_fails_when_the_routes_are_mapped()
    {
        var app = Builder(Issuing(), [("EmbedChannels:0:Id", "chan-a"), ("EmbedChannels:1:Id", "chan-b"), ("EmbedChannels:2:Id", "chan-a")]).Build();
        _apps.Add(app);

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapVaultAIEmbed());

        Assert.Contains("chan-a", ex.Message);
        Assert.Contains("twice", ex.Message);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void A_dot_segment_as_a_channel_id_fails_when_the_routes_are_mapped(string id)
    {
        // The upstream address is normalised, so the segment would vanish and the server's key would go to another endpoint.
        var inList = Builder(Issuing(), [("EmbedChannels:0:Id", id)]).Build();
        var single = Builder(Issuing(), [("ChannelId", id)]).Build();
        _apps.Add(inList);
        _apps.Add(single);

        Assert.Contains("dot segment", Assert.Throws<InvalidOperationException>(() => inList.MapVaultAIEmbed()).Message);
        Assert.Contains("dot segment", Assert.Throws<InvalidOperationException>(() => single.MapVaultAIEmbed()).Message);
    }

    [Fact]
    public void An_entry_with_only_a_label_fails_when_the_routes_are_mapped()
    {
        var app = Builder(Issuing(), [("EmbedChannels:0:Label", "Support")]).Build();
        _apps.Add(app);

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapVaultAIEmbed());

        Assert.Contains("EmbedChannels:0:Id", ex.Message);
    }

    [Fact]
    public async Task An_empty_label_is_no_label_and_AllowAnonymous_opens_the_list_to_an_unsigned_caller()
    {
        var client = await Host(Issuing(), vaultAI: [("EmbedChannels:0:Id", "chan-a"), ("EmbedChannels:0:Label", ""), ("AllowAnonymous", "true")]);

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ChannelsPath, perm: null));
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(body["channels"]![0]!["label"]);
    }

    [Fact]
    public async Task Ids_that_differ_only_in_case_are_two_channels()
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAI: [("EmbedChannels:0:Id", "Chan"), ("EmbedChannels:1:Id", "chan")]);

        using var upper = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("Chan")));
        using var lower = await client.SendAsync(Request(HttpMethod.Post, ChannelTokenPath("chan")));

        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(new string?[] { "https://vault.example.test/api/channels/Chan/token", "https://vault.example.test/api/channels/chan/token" },
            upstream.Calls.Select(c => c.Uri).ToArray());
    }

    [Fact]
    public async Task With_no_chat_service_address_the_channel_routes_are_not_mapped_either()
    {
        var client = await Host(Issuing(), vaultAI: [.. TwoChannels, ("Url", null)]);

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ChannelsPath));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
