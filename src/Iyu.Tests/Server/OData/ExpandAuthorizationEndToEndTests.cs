using System.Net;
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
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the reason ODataTestServerRoutingTests documents: a nested controller
// is not IsPublic and MVC's ControllerFeatureProvider skips it.

public sealed class NavSecret : IyuEntity
{
    public string Code { get; set; } = "";
}

public sealed class NavSecretExt : IyuEntity
{
    public string Code { get; set; } = "";
}

public sealed class NavOrder : IyuEntity
{
    public string Name { get; set; } = "";
    public Guid? SecretId { get; set; }
}

/// <summary>
/// A read type that carries a navigation to another read type. Nothing generates this shape today —
/// the Model target emits navigation on write entities only — so it is written by hand here, which
/// is the point: the question is what the framework does with such a type, and that has to be
/// answerable before a generator starts producing them.
/// </summary>
public sealed class NavOrderExt : IyuEntity
{
    public string Name { get; set; } = "";
    public Guid? SecretId { get; set; }
    public NavSecretExt? Secret { get; set; }
}

public sealed class NavContext(DbContextOptions<NavContext> options) : IyuDbContext(options)
{
    public DbSet<NavOrder> Orders => Set<NavOrder>();
    public DbSet<NavOrderExt> OrdersExt => Set<NavOrderExt>();
    public DbSet<NavSecret> Secrets => Set<NavSecret>();
    public DbSet<NavSecretExt> SecretsExt => Set<NavSecretExt>();
}

public sealed class NavOrdersController(NavContext ctx)
    : IyuODataController<NavOrderExt, NavOrder>(ctx);

public sealed class NavSecretsController(NavContext ctx)
    : IyuODataController<NavSecretExt, NavSecret>(ctx);

/// <summary>
/// Two questions about read-type navigation, answered through the real pipeline rather than argued
/// from the builder's documentation.
/// <para>
/// <b>Exposure</b> — <see cref="IyuEdmModelBuilder"/> registers read types on an
/// <c>ODataConventionModelBuilder</c>, which discovers navigation properties on its own. Whether
/// that actually reaches <c>$expand</c> has never been observable here, because no read type has
/// ever had a navigation property: a consumer measured <c>$expand</c> failing on every entity set
/// for exactly that reason.
/// </para>
/// <para>
/// <b>Authorization</b> — and the question that matters more. <c>RestrictPolicy</c> declares that
/// reading a set requires a policy. <c>$expand</c> is served by the <em>parent</em> set's
/// controller, so the child set's authorization filter never runs. If the expanded payload carries
/// the child's rows anyway, the declaration is not a read restriction — it restricts one route to
/// the data, not the data.
/// </para>
/// </summary>
public class ExpandAuthorizationEndToEndTests
{
    private const string OrdersSet = "NavOrders";
    private const string SecretsSet = "NavSecrets";
    private const string SecretReadPolicy = "secrets.read";
    private const string SecretWritePolicy = "secrets.write";

    private static readonly Guid SecretId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string SecretCode = "the-code-behind-the-policy";
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "nav-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(opts =>
        {
            opts.AddPolicy(SecretReadPolicy, p => p.RequireClaim("perm", SecretReadPolicy));
            opts.AddPolicy(SecretWritePolicy, p => p.RequireClaim("perm", SecretWritePolicy));
        });

        builder.Services.AddIyuMainServer<NavContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(NavOrdersController).Assembly);
                // Orders is open on purpose: the caller is meant to be allowed to read it.
                options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>(OrdersSet);
                options.ODataModel.AddEntityPair<NavSecretExt, NavSecret>(SecretsSet);
                options.ODataModel.RestrictPolicy(
                    SecretsSet, readPolicy: SecretReadPolicy, writePolicy: SecretWritePolicy);
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<NavContext>();
            ctx.SecretsExt.Add(new NavSecretExt { Id = SecretId, Code = SecretCode });
            ctx.OrdersExt.Add(new NavOrderExt
            {
                Id = OrderId, Name = "an order", SecretId = SecretId,
            });
            await ctx.SaveChangesAsync();
        }

        return app;
    }

    private static HttpRequestMessage Request(string path, string? perm = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (perm is not null) req.Headers.Add("X-Test-Perm", perm);
        return req;
    }

    /// <summary>
    /// Exposure, and the half of the guard that is easy to get wrong. The convention builder puts
    /// the read type's navigation in the EDM, so <c>$expand</c> resolves it and the related row is
    /// projected — <b>no framework change was needed for that</b>, which is the finding: a generator
    /// emitting these properties needs no companion change here.
    /// <para>
    /// A caller holding the target's read policy must still get the data. A guard that refuses an
    /// expand it should have allowed is as wrong as one that allows an expand it should have
    /// refused, and only this direction gets noticed late — by a consumer, not by a test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_read_types_navigation_reaches_expand_for_a_caller_that_may_read_the_target()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .SendAsync(Request($"/$data/{OrdersSet}?$expand=Secret", perm: SecretReadPolicy));

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("\"Secret\"", body, StringComparison.Ordinal);
            Assert.Contains(SecretCode, body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The route resolves a navigation name without regard to case, so the guard has to as well —
    /// in both directions. A caller holding the target's policy gets the rows for <c>secret</c>
    /// exactly as for <c>Secret</c>, and a caller without it is told to authenticate, not that the
    /// expression could not be read. Measured before the guard parsed with the route's services: the
    /// first was refused with 400 and the second answered 400 where the declared name answers 401.
    /// </summary>
    [Theory]
    [InlineData("secret")]
    [InlineData("SECRET")]
    public async Task A_navigation_named_in_another_case_is_authorized_like_its_declared_name(string name)
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();
            using var permitted = await client.SendAsync(
                Request($"/$data/{OrdersSet}?$expand={name}", perm: SecretReadPolicy));
            using var anonymous = await client.SendAsync(Request($"/$data/{OrdersSet}?$expand={name}"));

            Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
            Assert.Contains(SecretCode, await permitted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.DoesNotContain(SecretCode, await anonymous.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// An expand that touches nothing restricted is untouched — the guard costs an unprotected
    /// model nothing but the parse.
    /// </summary>
    [Fact]
    public async Task An_expand_naming_nothing_restricted_is_not_affected()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().GetAsync($"/$data/{OrdersSet}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The authenticated-but-insufficient case answers with 403, not 401 — the same split the
    /// framework's own <c>AuthorizeFilter</c> produces on the restricted set's own route, so a
    /// client cannot tell the two paths apart by status code.
    /// </summary>
    [Fact]
    public async Task An_authenticated_caller_without_the_targets_policy_is_forbidden()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .SendAsync(Request($"/$data/{OrdersSet}?$expand=Secret", perm: "something.else"));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The contrast that makes the next test a finding rather than a guess: the same anonymous
    /// caller cannot reach the protected set by its own route.
    /// </summary>
    [Fact]
    public async Task The_protected_set_refuses_the_same_caller_directly()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().GetAsync($"/$data/{SecretsSet}");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// 🔴 The security question. An anonymous caller reads an open set and expands into one whose
    /// read policy it does not hold. <c>RestrictPolicy</c> is enforced by an authorization filter on
    /// the protected set's own controller action, and an expand never reaches that action, so the
    /// filter has no opportunity to run.
    /// <para>
    /// Asserted as the property that has to hold — the protected row's payload must not appear —
    /// rather than as the behaviour that happens to hold today. If this fails, read-type navigation
    /// cannot be turned on until it passes: the generator emitting those properties is what makes
    /// this reachable at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Expand_must_not_carry_rows_from_a_set_the_caller_may_not_read()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .GetAsync($"/$data/{OrdersSet}?$expand=Secret");
            var body = await resp.Content.ReadAsStringAsync();

            Assert.DoesNotContain(SecretCode, body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The same two questions on the key route. An order addressed by key expands its secret for a
    /// caller that holds the read policy — the key route composes the expand into the query — and
    /// the same expand from an anonymous caller carries none of it. Before the key route composed
    /// the expand, the navigation came back unloaded for everyone: safe by accident, and wrong for
    /// the caller who was entitled to it. Pinning both halves here is what keeps the fix from
    /// turning into the leak.
    /// </summary>
    [Fact]
    public async Task The_key_route_expands_for_a_permitted_caller_and_carries_nothing_otherwise()
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();
            using var permitted = await client.SendAsync(
                Request($"/$data/{OrdersSet}({OrderId})?$expand=Secret", perm: SecretReadPolicy));
            var permittedBody = await permitted.Content.ReadAsStringAsync();
            using var anonymous = await client.GetAsync($"/$data/{OrdersSet}({OrderId})?$expand=Secret");
            var anonymousBody = await anonymous.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
            Assert.Contains(SecretCode, permittedBody, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.DoesNotContain(SecretCode, anonymousBody, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }
}
