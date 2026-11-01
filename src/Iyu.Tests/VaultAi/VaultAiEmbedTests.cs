using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Iyu.Tests.Server.OData;
using Iyu.VaultAi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// <c>MapVaultAiEmbed()</c> — the server half of embedding the chat widget. Through the real TestServer pipeline, with the
/// chat service replaced by a fake handler so what is sent to it and what is let out are both observable.
/// </summary>
public sealed class VaultAiEmbedTests : IDisposable
{
    private const string Policy = "ChatUsers";
    private const string ServerKey = "server-key-123";
    private const string ConfigPath = "/api/vault-ai-embed/config";
    private const string TokenPath = "/api/vault-ai-embed/token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "iyu-vaultai-embed-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _apps = [];

    public VaultAiEmbedTests() => Directory.CreateDirectory(_root);

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

    private WebApplicationBuilder Builder(Upstream upstream, (string Key, string? Value)[]? vaultAi = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["VaultAi:Url"] = "https://vault.example.test",
            ["VaultAi:Token"] = ServerKey,
            ["VaultAi:ChannelId"] = "chan-1",
            ["VaultAi:ReportPath"] = _root,
        };
        foreach (var (key, value) in vaultAi ?? []) settings["VaultAi:" + key] = value;
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, p => p.RequireClaim("perm", Policy));
        builder.Services.AddVaultAiReports(builder.Configuration);
        // Registered after the module's own, so this handler is the one the token call ends up using.
        builder.Services.AddHttpClient(VaultAiEmbedExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => upstream);
        return builder;
    }

    private async Task<HttpClient> Host(Upstream upstream, (string Key, string? Value)[]? vaultAi = null, bool withReports = false)
    {
        var app = Builder(upstream, vaultAi).Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (withReports) app.UseVaultAiReports();
        app.MapVaultAiEmbed();
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
        var client = await Host(upstream, vaultAi: [("AuthorizationPolicy", Policy)]);

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
        var client = await Host(Issuing(), vaultAi: [("AllowAnonymous", "true")]);

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

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapVaultAiEmbed());

        Assert.Contains("NoSuchPolicy", ex.Message);
    }

    [Fact]
    public async Task The_routes_coexist_with_the_report_module_under_the_same_policy()
    {
        var client = await Host(Issuing(), withReports: true, vaultAi: [("AuthorizationPolicy", Policy)]);

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
        var client = await Host(Issuing(), vaultAi: [("Url", "https://vault.example.test/")]);

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
        var client = await Host(Issuing(), vaultAi: [("Url", "http://10.0.0.5:8080"), ("EmbedOrigin", "https://chat.example.test/")]);

        using var resp = await client.SendAsync(Request(HttpMethod.Get, ConfigPath));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal("https://chat.example.test", (string?)JsonNode.Parse(text)!["origin"]);
        Assert.DoesNotContain("10.0.0.5", text);
    }

    [Fact]
    public async Task Without_a_channel_config_says_not_enabled_and_token_says_not_configured()
    {
        var upstream = Issuing();
        var client = await Host(upstream, vaultAi: [("ChannelId", null)]);

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
        var client = await Host(Issuing(), vaultAi: [("Url", null)]);

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
        var client = await Host(upstream, vaultAi: [("ChannelId", "a b/c")]);

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
        var client = await Host(upstream, vaultAi: [("EmbedTokenTimeout", "00:00:00.100")]);
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VaultAi:Url"] = "https://vault.example.test", ["VaultAi:Token"] = "t" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVaultAiReports(config);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(VaultAiEmbedExtensions.HttpClientName);

        // Otherwise the HTTP client's own hundred seconds would end a longer VaultAi:EmbedTokenTimeout early.
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
}
