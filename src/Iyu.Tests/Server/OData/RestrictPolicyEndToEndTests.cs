using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the same reason ODataTestServerRoutingTests documents: a nested
// controller is not IsPublic and MVC's ControllerFeatureProvider skips it.

public sealed class PolicyWidget : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class PolicyWidgetExt : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class PolicyWidgetContext(DbContextOptions<PolicyWidgetContext> options) : IyuDbContext(options)
{
    public DbSet<PolicyWidget> Widgets => Set<PolicyWidget>();
    public DbSet<PolicyWidgetExt> WidgetsExt => Set<PolicyWidgetExt>();
}

public sealed class PolicyWidgetsController(PolicyWidgetContext ctx)
    : IyuODataController<PolicyWidgetExt, PolicyWidget>(ctx);

/// <summary>
/// A minimal authentication handler for tests: a request carrying <c>X-Test-Perm</c> headers
/// authenticates as a user holding one <c>perm</c> claim per header value; a request with none is
/// left unauthenticated (<see cref="AuthenticateResult.NoResult"/>) rather than rejected outright,
/// the same shape a real bearer-token handler gives an anonymous caller.
/// </summary>
internal sealed class HeaderClaimAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Perm", out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            values.Select(v => new Claim("perm", v!)), authenticationType: "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// docket #179 end-to-end: through the real TestServer pipeline — not a bare DI container — a
/// GET/POST on a set registered with <c>IyuEdmModelBuilder.RestrictPolicy</c> is actually gated by
/// ASP.NET Core's <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>, the same
/// depth <see cref="ODataTestServerRoutingTests"/> already holds <c>Restrict</c>(verbs) to.
/// </summary>
public class RestrictPolicyEndToEndTests
{
    private const string Set = "PolicyWidgets";
    private const string ReadPolicy = "widgets.read";
    private const string WritePolicy = "widgets.write";

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        // AddIyuMainServer's own AddAuthorizationCore (gated on RestrictPolicy usage) would add a
        // second, policy-less registration if this ran first — adding the real policies directly
        // here, as a consumer's own AddIyuIdentity call would, is what the framework code expects.
        builder.Services.AddAuthorization(opts =>
        {
            opts.AddPolicy(ReadPolicy, p => p.RequireClaim("perm", ReadPolicy));
            opts.AddPolicy(WritePolicy, p => p.RequireClaim("perm", WritePolicy));
        });

        builder.Services.AddIyuMainServer<PolicyWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("policy-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(PolicyWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<PolicyWidgetExt, PolicyWidget>(Set);
                options.ODataModel.RestrictPolicy(Set, readPolicy: ReadPolicy, writePolicy: WritePolicy);
            });

        var app = builder.Build();
        app.UseAuthentication();   // before UseIyuMainServer's UseRouting/MapControllers — HttpContext.User must be set before the MVC action pipeline runs.
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? perm = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (perm is not null) req.Headers.Add("X-Test-Perm", perm);
        return req;
    }

    [Fact]
    public async Task Get_without_authentication_is_challenged()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .SendAsync(Request(HttpMethod.Get, $"/$data/{Set}"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Get_with_the_wrong_claim_is_forbidden()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .SendAsync(Request(HttpMethod.Get, $"/$data/{Set}", perm: "something.else"));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Get_with_the_read_claim_succeeds()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .SendAsync(Request(HttpMethod.Get, $"/$data/{Set}", perm: ReadPolicy));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The read and write claims are enforced independently — holding the read claim must not be
    /// enough to write, which is exactly the asymmetry a single blanket policy could not express
    /// and the whole reason <c>RestrictPolicy</c> takes two separate parameters.
    /// </summary>
    [Fact]
    public async Task Post_with_only_the_read_claim_is_forbidden()
    {
        var app = await StartAsync();
        try
        {
            var req = Request(HttpMethod.Post, $"/$data/{Set}", perm: ReadPolicy);
            req.Content = JsonContent.Create(new { Name = "should not be created" });
            using var resp = await app.GetTestServer().CreateClient().SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Post_with_the_write_claim_succeeds()
    {
        var app = await StartAsync();
        try
        {
            var req = Request(HttpMethod.Post, $"/$data/{Set}", perm: WritePolicy);
            req.Content = JsonContent.Create(new { Name = "a real widget" });
            using var resp = await app.GetTestServer().CreateClient().SendAsync(req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Patch_and_delete_are_also_gated_by_the_write_claim(string method)
    {
        var app = await StartAsync();
        try
        {
            var id = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PolicyWidgetContext>();
                db.Widgets.Add(new PolicyWidget { Id = id, Name = "seed" });
                await db.SaveChangesAsync();
            }

            var req = Request(new HttpMethod(method), $"/$data/{Set}({id})", perm: ReadPolicy);   // wrong claim
            if (method == "PATCH") req.Content = JsonContent.Create(new { Name = "changed" });
            using var resp = await app.GetTestServer().CreateClient().SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// An unregistered policy name is not a "fail closed" case (unlike the GraphQL bridge, which
    /// explicitly checks and returns a clean GraphQL error) — ASP.NET Core MVC's own standard
    /// <c>AuthorizeFilter</c> throws an <see cref="InvalidOperationException"/> for it, identical to
    /// what a hand-written <c>[Authorize(Policy = "typo")]</c> would throw anywhere else in the same
    /// app. Since <c>AddIyuMainServer</c> now wires a global <c>IExceptionHandler</c>/
    /// <c>AddProblemDetails()</c> pair (G-1, <c>ROADMAP.md</c> §2) so that no unhandled exception —
    /// not just a write-path <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> — reaches
    /// a client raw, that same exception no longer escapes the TestServer call: it is caught and
    /// turned into a structured 500 <c>ProblemDetails</c> like every other unhandled exception this
    /// framework's pipeline sees. Pinned here so a future change to either convention does not
    /// silently alter this to something else.
    /// </summary>
    [Fact]
    public async Task Get_with_an_unregistered_policy_name_returns_a_structured_500_naming_the_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<PolicyWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("probe-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(PolicyWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<PolicyWidgetExt, PolicyWidget>(Set);
                options.ODataModel.RestrictPolicy(Set, readPolicy: "never.registered");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().GetAsync($"/$data/{Set}");
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        }
        finally { await app.DisposeAsync(); }
    }
}
