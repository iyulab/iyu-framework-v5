using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Iyu.VaultAI;
using Iyu.VaultAI.FolderChatbots;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// How each chatbot stands, and how the embed route offers it: states as codes (the screen chooses the words), a summary call that does not
/// answer making one line unknown rather than holding the list, and the list shown to a signed-in caller only.
/// </summary>
public sealed class FolderChatbotStatusTests : IDisposable
{
    private readonly List<WebApplication> _apps = [];

    public void Dispose()
    {
        foreach (var app in _apps) app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class MemoryStore : IFolderChatbotManifestStore
    {
        public string? Json;
        public string? Read() => Json;
        public void Write(string json) => Json = json;
        public void Backup() { }
    }

    private sealed class Setup
    {
        public FakeFolderChatbotAdmin Admin { get; } = new();
        public FakeFolderChatbotSource Source { get; } = new();
        public FolderChatbotSyncService Service { get; }
        public FolderChatbotStatusReader Reader { get; }

        public Setup()
        {
            Admin.Models.Add(new ModelRef(Guid.NewGuid(), "language", "chat", "Chat", 131_072));
            Admin.Models.Add(new ModelRef(Guid.NewGuid(), "embedding", "embed", "Embed", null));
            Service = new FolderChatbotSyncService(new VaultAIFolderChatbotOptions { Enabled = true, Path = "/docs", Prefix = "kb-" },
                Admin, Source, new MemoryStore(), "https://v.example.test", NullLogger.Instance);
            Reader = new FolderChatbotStatusReader(Admin, Source, Service, NullLogger<FolderChatbotStatusReader>.Instance);
        }

        public async Task SyncAsync() => await Service.RunOnceAsync(default);

        public static StorageSummary Summary(int indexed = 0, int pending = 0, int failed = 0, int notIndexable = 0)
            => new(indexed + pending + failed + notIndexable, new Dictionary<string, int>
            {
                ["indexed"] = indexed, ["pending"] = pending, ["failed"] = failed, ["not_indexable"] = notIndexable,
            });
    }

    // ---------------- the states ----------------

    [Fact]
    public async Task A_folder_the_engine_has_not_made_a_chatbot_of_yet_is_pending_create()
    {
        var s = new Setup();
        s.Source.Put("handbook", "a.md", "a");

        var status = Assert.Single(await s.Reader.GetAsync(default));

        Assert.Equal(("handbook", "pending_create"), (status.Name, status.State));
    }

    [Theory]
    [InlineData(5, 0, 0, 0, "ready")]
    [InlineData(3, 2, 0, 0, "indexing")]
    [InlineData(2, 1, 1, 0, "indexing")]   // work still going on outranks a failure: the failure shows in the counts
    [InlineData(4, 0, 1, 0, "error")]
    [InlineData(4, 0, 0, 1, "not_indexable")]
    [InlineData(0, 0, 0, 0, "empty")]
    public async Task A_chatbot_is_in_the_first_state_that_applies_and_the_counts_say_what_it_is_made_of(
        int indexed, int pending, int failed, int notIndexable, string expected)
    {
        var s = new Setup();
        s.Source.Put("handbook", "a.md", "a");
        await s.SyncAsync();
        s.Admin.SummaryOf = (_, _) => Task.FromResult(Setup.Summary(indexed, pending, failed, notIndexable));

        var status = Assert.Single(await s.Reader.GetAsync(default));

        Assert.Equal(expected, status.State);
        Assert.Equal(pending, status.InProgress);
        Assert.Equal(failed, status.Failed);
        Assert.Equal(notIndexable, status.NotIndexable);
    }

    [Fact]
    public async Task Files_the_server_refused_count_toward_the_total_and_make_a_ready_chatbot_unsupported()
    {
        var s = new Setup();
        s.Source.Put("handbook", "a.md", "a");
        s.Source.Put("handbook", "odd.md", "x");
        s.Admin.UploadFailure = name => name.EndsWith("odd.md", StringComparison.Ordinal) ? new VaultAIUnsupportedFileException("no") : null;
        await s.SyncAsync();
        s.Admin.SummaryOf = (_, _) => Task.FromResult(Setup.Summary(indexed: 1));

        var status = Assert.Single(await s.Reader.GetAsync(default));

        Assert.Equal("unsupported", status.State);
        Assert.Equal(2, status.Total);   // the refused file is not in the storage but is part of the folder
        Assert.Equal(1, status.Skipped);
    }

    [Fact]
    public async Task A_bot_waiting_out_its_grace_period_is_pending_delete()
    {
        var s = new Setup();
        s.Source.Put("handbook", "a.md", "a");
        s.Source.Put("faq", "q.md", "q");
        await s.SyncAsync();
        s.Source.Bots.Remove("faq");
        await s.SyncAsync();

        var statuses = await s.Reader.GetAsync(default);

        Assert.Equal("pending_delete", statuses.Single(b => b.Name == "faq").State);
    }

    [Fact]
    public async Task A_summary_that_does_not_answer_makes_that_one_line_unknown_and_the_rest_are_still_read()
    {
        var s = new Setup();
        s.Source.Put("handbook", "a.md", "a");
        s.Source.Put("faq", "q.md", "q");
        await s.SyncAsync();
        s.Admin.SummaryOf = async (tags, ct) =>
        {
            if (tags!.Contains(FolderChatbotNames.TagOf("faq"))) await Task.Delay(Timeout.Infinite, ct);
            return Setup.Summary(indexed: 3);
        };
        s.Reader.SummaryTimeout = TimeSpan.FromMilliseconds(100);

        var statuses = await s.Reader.GetAsync(default);

        Assert.Equal("unknown", statuses.Single(b => b.Name == "faq").State);
        Assert.Equal("ready", statuses.Single(b => b.Name == "handbook").State);
    }

    // ---------------- the embed route ----------------

    private async Task<HttpClient> HostAsync(Setup s, bool anonymous, bool withEngine = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VaultAI:Url"] = "https://vault.example.test",
            ["VaultAI:Token"] = "k",
            ["VaultAI:ChannelId"] = "chan-1",
            ["VaultAI:AllowAnonymous"] = anonymous ? "true" : "false",
            ["VaultAI:ReportPath"] = Path.GetTempPath(),
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, SignedInHandler>("Test", null);
        builder.Services.AddVaultAIReports(builder.Configuration);
        if (withEngine) builder.Services.AddSingleton(s.Reader);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapVaultAIEmbed();
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestServer().CreateClient();
    }

    private sealed class SignedInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Signed-In")) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", "u")], authenticationType: "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private static HttpRequestMessage Get(bool signedIn)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/vault-ai-embed/config");
        if (signedIn) request.Headers.Add("X-Signed-In", "1");
        return request;
    }

    [Fact]
    public async Task A_signed_in_caller_gets_the_chatbots_beside_the_channel()
    {
        var s = new Setup();
        s.Source.Put("handbook", "a.md", "a");
        await s.SyncAsync();
        s.Admin.SummaryOf = (_, _) => Task.FromResult(Setup.Summary(indexed: 2));
        var client = await HostAsync(s, anonymous: false);

        using var response = await client.SendAsync(Get(signedIn: true));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("chan-1", (string?)body["channelId"]);
        var bot = Assert.Single(body["bots"]!.AsArray())!;
        Assert.Equal("handbook", (string?)bot["name"]);
        Assert.Equal("ready", (string?)bot["state"]);
        Assert.Equal(2, (int?)bot["indexed"]);
    }

    [Fact]
    public async Task An_anonymous_caller_where_anonymous_use_is_allowed_gets_the_channel_but_not_the_chatbots()
    {
        var s = new Setup();
        s.Source.Put("handbook", "a.md", "a");
        await s.SyncAsync();
        var client = await HostAsync(s, anonymous: true);

        using var response = await client.SendAsync(Get(signedIn: false));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("chan-1", (string?)body["channelId"]);
        Assert.Null(body["bots"]);
    }

    [Fact]
    public async Task A_host_without_the_engine_gets_the_answer_it_always_got()
    {
        var s = new Setup();
        var client = await HostAsync(s, anonymous: false, withEngine: false);

        using var response = await client.SendAsync(Get(signedIn: true));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(["enabled", "origin", "channelId"], body.Select(p => p.Key));
    }
}
