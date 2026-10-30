using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Iyu.Lowline;
using Lowline;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// A request authenticates as the user named in <c>X-Test-User</c>, holding one <c>perm</c> claim per <c>X-Test-Perm</c>
/// header; with neither header it is left unauthenticated, like a bearer handler given an anonymous caller.
/// </summary>
internal sealed class LowlineTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
        claims.AddRange(Request.Headers["X-Test-Perm"].Select(p => new Claim("perm", p!)));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class IyuLowlineEndpointTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private readonly OrderHook _hook = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync() => StartAsync();

    public async Task DisposeAsync()
    {
        var forms = _app.Services.GetRequiredService<LowlineForms>();
        foreach (var space in new[] { "plant-a", "plant-b" })
            await forms.WhenTunedAsync(new LowlineScope("order", space));
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private async Task StartAsync(Action<IyuLowlineOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, LowlineTestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("lowline.use", p => p.RequireClaim("perm", "lowline.use")));
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", OrderForm);
            configure?.Invoke(o);
        });
        builder.Services.AddScoped<ILowlineFormHook>(_ => _hook);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapIyuLowline();
        await _app.StartAsync();
        using (var scope = _app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
        _client = _app.GetTestClient();
    }

    private async Task<IReadOnlyList<SettledRecord>> Stored(string space = "plant-a") =>
        await _app.Services.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", space));

    private static HttpRequestMessage Post(string url, string json, string? user = "alice", string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, contentType),
        };
        if (user is not null) request.Headers.Add("X-Test-User", user);
        return request;
    }

    private const string SettleBody = """{"id":"r1","draft":null}""";

    [Fact]
    public async Task Both_routes_refuse_a_caller_who_is_not_signed_in()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        var propose = await _client.SendAsync(Post("/lowline/order/propose", """{"id":"r1","values":{}}""", user: null));
        var settle = await _client.SendAsync(Post("/lowline/order/settle", SettleBody, user: null));

        Assert.Equal(HttpStatusCode.Unauthorized, propose.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, settle.StatusCode);
        Assert.Empty(await Stored());
        Assert.Equal(0, _hook.Reads);
    }

    [Fact]
    public async Task A_signed_in_caller_settles_a_saved_record_and_gets_the_engines_response_shape()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        var response = await _client.SendAsync(Post("/lowline/order/settle", SettleBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("r1", body.GetProperty("id").GetString());
        Assert.True(body.TryGetProperty("settledAt", out _));
        Assert.Single(await Stored());
    }

    [Fact]
    public async Task Values_in_a_settle_request_are_not_read_the_stored_row_is_what_is_learned()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        // The engine's own web host takes the values from the request when no hook reads them; this one never does.
        var body = """{"id":"r1","saved":{"customer":"globex","colour":"blue"},"draft":null}""";
        var response = await _client.SendAsync(Post("/lowline/order/settle", body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var values = JsonDocument.Parse(Assert.Single(await Stored()).Json).RootElement.GetProperty("values");
        Assert.Equal("red", values.GetProperty("colour").GetString());
        Assert.Equal("acme", values.GetProperty("customer").GetString());
    }

    [Fact]
    public async Task A_forged_draft_over_http_does_not_sign_the_record()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");
        var body = """{"id":"r1","draft":{"opened":{},"fields":{"colour":{"held":0.5}},"by":"mallory","batch":"evil"}}""";

        await _client.SendAsync(Post("/lowline/order/settle", body));

        var record = JsonDocument.Parse(Assert.Single(await Stored()).Json).RootElement;
        Assert.False(record.TryGetProperty("settled_by", out _));
        Assert.False(record.TryGetProperty("batch", out _));
    }

    [Fact]
    public async Task Someone_elses_record_and_a_missing_one_are_both_404_and_nothing_is_stored()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        var theirs = await _client.SendAsync(Post("/lowline/order/settle", SettleBody, user: "mallory"));
        var missing = await _client.SendAsync(Post("/lowline/order/settle", """{"id":"nope","draft":null}""", user: "mallory"));

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task A_form_that_is_not_defined_is_404_on_both_routes()
    {
        var propose = await _client.SendAsync(Post("/lowline/nope/propose", """{"id":"r1","values":{}}"""));
        var settle = await _client.SendAsync(Post("/lowline/nope/settle", SettleBody));

        Assert.Equal(HttpStatusCode.NotFound, propose.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, settle.StatusCode);
    }

    [Fact]
    public async Task A_refused_settle_is_400_and_does_not_repeat_the_stored_value()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "stored-secret-value");

        var response = await _client.SendAsync(Post("/lowline/order/settle", SettleBody));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("stored-secret-value", await response.Content.ReadAsStringAsync());
        Assert.Empty(await Stored());
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Origin", "https://evil.example")]
    public async Task A_request_from_another_site_is_403_and_nothing_is_stored(string header, string value)
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");
        var request = Post("/lowline/order/settle", SettleBody);
        request.Headers.Add(header, value);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await Stored());
        Assert.Equal(0, _hook.Reads);
    }

    [Fact]
    public async Task A_same_origin_browser_request_is_accepted()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");
        var request = Post("/lowline/order/settle", SettleBody);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", "http://localhost");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_form_post_is_not_json_and_is_415()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        var response = await _client.SendAsync(Post("/lowline/order/settle", "id=r1", contentType: "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413_before_it_is_read()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");
        var huge = "{\"id\":\"r1\",\"draft\":{\"pad\":\"" + new string('x', 300_000) + "\"}}";

        var response = await _client.SendAsync(Post("/lowline/order/settle", huge));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, _hook.Reads);
    }

    [Fact]
    public async Task A_body_with_no_declared_length_is_cut_off_at_the_limit_too()
    {
        _hook.Save("r1", "alice", "plant-a", "acme", "red");
        var huge = "{\"id\":\"r1\",\"draft\":{\"pad\":\"" + new string('x', 300_000) + "\"}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/lowline/order/settle") { Content = new UndeclaredLengthContent(huge) };
        request.Headers.Add("X-Test-User", "alice");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, _hook.Reads);
    }

    /// <summary>A JSON body sent chunked: the request carries no <c>Content-Length</c> to check against the limit.</summary>
    private sealed class UndeclaredLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UndeclaredLengthContent(string json)
        {
            _bytes = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task A_body_that_does_not_read_is_400()
    {
        var response = await _client.SendAsync(Post("/lowline/order/settle", "{ not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Propose_answers_what_to_show_from_the_records_settled_so_far()
    {
        for (var i = 0; i < 40; i++)
        {
            _hook.Save($"r{i}", "alice", "plant-a", "acme", "red");
            await _client.SendAsync(Post("/lowline/order/settle", $$"""{"id":"r{{i}}","draft":null}"""));
        }

        await _app.Services.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-a"));
        var response = await _client.SendAsync(Post("/lowline/order/propose", """{"id":"new","values":{"customer":"acme"}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shown = await response.Content.ReadFromJsonAsync<JsonElement>();
        var colour = shown.EnumerateArray().Single(p => p.GetProperty("field").GetString() == "colour");
        Assert.Equal("red", colour.GetProperty("suggestion").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Propose_takes_its_memory_space_from_the_hook_not_the_request()
    {
        for (var i = 0; i < 40; i++)
        {
            _hook.Save($"r{i}", "alice", "plant-a", "acme", "red");
            await _client.SendAsync(Post("/lowline/order/settle", $$"""{"id":"r{{i}}","draft":null}"""));
        }

        await _app.Services.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-a"));
        // The request names a space; the hook, which answers plant-a for this caller, is the only thing that decides.
        var response = await _client.SendAsync(Post(
            "/lowline/order/propose", """{"id":"new","space":"plant-b","values":{"customer":"acme"}}"""));

        var shown = await response.Content.ReadFromJsonAsync<JsonElement>();
        var colour = shown.EnumerateArray().Single(p => p.GetProperty("field").GetString() == "colour");
        Assert.Equal("red", colour.GetProperty("suggestion").GetProperty("value").GetString());
    }
}

public sealed class IyuLowlineEndpointPolicyTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, LowlineTestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("lowline.use", p => p.RequireClaim("perm", "lowline.use")));
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", OrderForm);
            o.Policy = "lowline.use";
        });
        builder.Services.AddScoped<ILowlineFormHook, OrderHook>();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapIyuLowline();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    [Fact]
    public async Task A_signed_in_caller_without_the_policy_is_403_and_one_with_it_gets_through()
    {
        var without = new HttpRequestMessage(HttpMethod.Post, "/lowline/order/settle")
        {
            Content = new StringContent("""{"id":"r1","draft":null}""", Encoding.UTF8, "application/json"),
        };
        without.Headers.Add("X-Test-User", "alice");
        var with = new HttpRequestMessage(HttpMethod.Post, "/lowline/order/settle")
        {
            Content = new StringContent("""{"id":"r1","draft":null}""", Encoding.UTF8, "application/json"),
        };
        with.Headers.Add("X-Test-User", "alice");
        with.Headers.Add("X-Test-Perm", "lowline.use");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(without)).StatusCode);
        // Past the policy the record does not exist for this hook, which is a 404 — not a 401 or 403.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(with)).StatusCode);
    }
}
