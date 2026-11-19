using System.Net;
using Iyu.Tests.Server.OData;
using Iyu.VaultAI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// The module is one middleware serving a report API, a viewer and the report folders; none of it may answer a caller
/// the host's authorization policy has not admitted. Through the real TestServer pipeline.
/// </summary>
public sealed class VaultAIAuthorizationTests : IDisposable
{
    private const string Policy = "ReportsAdmin";
    private const string EditPolicy = "ReportsEditor";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iyu-vaultai-authz-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplication> _apps = [];

    public VaultAIAuthorizationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var app in _apps) app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<HttpClient> Host(params (string Key, string? Value)[] vaultAI)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["VaultAI:Url"] = "https://vault.example.test",
            ["VaultAI:Token"] = "t",
            ["VaultAI:ReportPath"] = _root,
        };
        foreach (var (key, value) in vaultAI) settings["VaultAI:" + key] = value;
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, p => p.RequireClaim("perm", Policy))
            .AddPolicy(EditPolicy, p => p.RequireClaim("perm", EditPolicy));
        builder.Services.AddVaultAIReports(builder.Configuration);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseVaultAIReports();
        app.MapGet("/health", () => "ok");
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestServer().CreateClient();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? perm = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (perm is not null) req.Headers.Add("X-Test-Perm", perm);
        return req;
    }

    // Every kind of route the module owns: the API, the viewer, the report folders (browsing), and the GET that starts a generation.
    public static TheoryData<string, string> ModuleRoutes => new()
    {
        { "GET", "/api/vault-ai/reports" },
        { "DELETE", "/api/vault-ai/reports/daily/outputs/2026-01-01.md" },
        { "POST", "/api/vault-ai/reports/daily/generate" },
        { "PATCH", "/api/vault-ai/reports/daily" },
        { "GET", "/vault-ai-reports" },
        { "GET", "/vault-ai-reports/daily/new" },
        { "GET", "/vault-ai-reports-files/" },
        { "GET", "/VAULT-AI-REPORTS-FILES/" },
    };

    [Theory]
    [MemberData(nameof(ModuleRoutes))]
    public async Task An_anonymous_caller_is_challenged_on_every_route_of_the_module(string method, string path)
    {
        var client = await Host();

        using var resp = await client.SendAsync(Request(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_user_is_served_when_no_policy_is_named()
    {
        var client = await Host();

        using var resp = await client.SendAsync(Request(HttpMethod.Get, "/api/vault-ai/reports", perm: "anything"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/vault-ai/reports")]
    [InlineData("/vault-ai-reports-files/")]
    public async Task A_named_policy_forbids_a_user_who_does_not_meet_it_and_admits_one_who_does(string path)
    {
        var client = await Host(("AuthorizationPolicy", Policy));

        using var forbidden = await client.SendAsync(Request(HttpMethod.Get, path, perm: "other"));
        using var admitted = await client.SendAsync(Request(HttpMethod.Get, path, perm: Policy));
        using var anonymous = await client.SendAsync(Request(HttpMethod.Get, path));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // Reading and generating stay with the module's policy; editing a report and deleting an output also need the edit
    // policy when one is named. A user who passes only the first is refused on exactly those two routes — and the
    // folder does not exist, so a request that gets past the check ends in the handler's own answer, never 401 or 403.
    [Theory]
    [InlineData("PATCH", "/api/vault-ai/reports/daily")]
    [InlineData("DELETE", "/api/vault-ai/reports/daily/outputs/2026-01-01.md")]
    public async Task A_named_edit_policy_holds_editing_and_deleting_to_it(string method, string path)
    {
        var client = await Host(("ReportEditPolicy", EditPolicy));

        using var reader = await client.SendAsync(Request(new HttpMethod(method), path, perm: "anything"));
        using var editor = await client.SendAsync(Request(new HttpMethod(method), path, perm: EditPolicy));
        using var anonymous = await client.SendAsync(Request(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, editor.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, editor.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/vault-ai/reports")]
    [InlineData("POST", "/api/vault-ai/reports/daily/generate")]
    [InlineData("GET", "/vault-ai-reports")]
    public async Task A_named_edit_policy_leaves_reading_and_generating_to_the_module_policy(string method, string path)
    {
        var client = await Host(("ReportEditPolicy", EditPolicy));

        using var reader = await client.SendAsync(Request(new HttpMethod(method), path, perm: "anything"));

        Assert.NotEqual(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, reader.StatusCode);
    }

    // Both policies are held when both are named: the edit policy is a second check, not a replacement for the first.
    [Fact]
    public async Task The_edit_policy_adds_to_the_module_policy_rather_than_replacing_it()
    {
        var client = await Host(("AuthorizationPolicy", Policy), ("ReportEditPolicy", EditPolicy));

        using var editorOnly = await client.SendAsync(Request(HttpMethod.Patch, "/api/vault-ai/reports/daily", perm: EditPolicy));

        Assert.Equal(HttpStatusCode.Forbidden, editorOnly.StatusCode);   // the module's policy refuses before the edit one is read
    }

    [Fact]
    public async Task Without_an_edit_policy_a_signed_in_user_edits_as_before()
    {
        var client = await Host();

        using var resp = await client.SendAsync(Request(HttpMethod.Patch, "/api/vault-ai/reports/daily", perm: "anything"));

        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task An_edit_policy_that_is_not_registered_fails_at_startup_naming_the_key()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Host(("ReportEditPolicy", "NoSuchPolicy")));

        Assert.Contains("VaultAI:ReportEditPolicy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NoSuchPolicy", ex.Message, StringComparison.Ordinal);
    }

    // An anonymous module has no signed-in user to hold to a policy; naming one would read as protection that is not
    // there, so it is refused rather than ignored.
    [Fact]
    public async Task An_edit_policy_beside_anonymous_access_fails_at_startup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Host(("AllowAnonymous", "true"), ("ReportEditPolicy", EditPolicy)));

        Assert.Contains("VaultAI:ReportEditPolicy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AllowAnonymous", ex.Message, StringComparison.Ordinal);
    }

    // GET .../new starts an LLM call, so a signed-in user must not be sendable there by another site. The folder does
    // not exist, so a request that gets past the check ends 404 without starting anything.
    [Theory]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.NotFound)]
    [InlineData("none", HttpStatusCode.NotFound)]
    [InlineData(null, HttpStatusCode.NotFound)]
    public async Task The_generation_trigger_refuses_a_request_another_site_started(string? site, HttpStatusCode expected)
    {
        var client = await Host();
        var req = Request(HttpMethod.Get, "/vault-ai-reports/missing/new", perm: "anything");
        if (site is not null) req.Headers.Add("Sec-Fetch-Site", site);

        using var resp = await client.SendAsync(req);

        Assert.Equal(expected, resp.StatusCode);
    }

    // Without Sec-Fetch-Site (an older browser) the request's Origin or Referer is read the same way.
    [Theory]
    [InlineData("Origin", "https://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("Origin", "null", HttpStatusCode.Forbidden)]
    [InlineData("Origin", "http://localhost:8080", HttpStatusCode.Forbidden)]   // same host, other port: another origin
    [InlineData("Referer", "https://evil.example/page", HttpStatusCode.Forbidden)]
    [InlineData("Origin", "http://localhost", HttpStatusCode.NotFound)]
    [InlineData("Referer", "http://localhost/vault-ai-reports/missing", HttpStatusCode.NotFound)]
    public async Task Without_fetch_metadata_the_origin_or_referer_decides(string header, string value, HttpStatusCode expected)
    {
        var client = await Host();
        var req = Request(HttpMethod.Get, "/vault-ai-reports/missing/new", perm: "anything");
        req.Headers.Add(header, value);

        using var resp = await client.SendAsync(req);

        Assert.Equal(expected, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_access_is_an_explicit_opt_in()
    {
        var client = await Host(("AllowAnonymous", "true"));

        using var resp = await client.GetAsync("/api/vault-ai/reports");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task A_path_the_module_does_not_own_is_left_to_the_host()
    {
        var client = await Host();

        using var resp = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task A_policy_name_that_is_not_registered_fails_at_startup_not_at_the_first_request()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Host(("AuthorizationPolicy", "NoSuchPolicy")));

        Assert.Contains("NoSuchPolicy", ex.Message);
    }
}
