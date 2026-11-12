using System.Security.Claims;
using Iyu.MainServer;
using Iyu.MainServer.ML;
using Iyu.ML;
using Iyu.ML.EntityFrameworkCore;
using Iyu.Tests.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.ML;

/// <summary>
/// A host that serves its entities through the main server declared read policies there; a dataset honours them without the
/// dataset author reading them again. These tests build the real host, declare the policies the way an application does, and
/// ask what an export may read.
/// </summary>
public sealed class MainServerMLReadGuardTests
{
    private const string NamesRead = "names.read";
    private const string OrdersRead = "orders.read";

    private static ClaimsPrincipal Caller(params string[] permissions) =>
        new(new ClaimsIdentity([new Claim("sub", "analyst"), .. permissions.Select(p => new Claim("perm", p))], "test"));

    private static async Task<WebApplication> StartAsync(
        Action<IyuMainServerOptions> policies,
        Action<MLEntityFrameworkBuilder<NavContext>> datasets,
        Action<AuthorizationOptions>? authorization = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(NamesRead, p => p.RequireClaim("perm", NamesRead));
            o.AddPolicy(OrdersRead, p => p.RequireClaim("perm", OrdersRead));
            authorization?.Invoke(o);
        });
        var dbName = "ml-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<NavContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(NavOrdersController).Assembly);
                options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>("NavOrders");
                options.ODataModel.AddEntityPair<NavSecretExt, NavSecret>("NavSecrets");
                policies(options);
            });
        builder.Services.AddIyuMLEntityFramework<NavContext>(datasets);
        builder.Services.AddIyuMLMainServerReadGuard();
        builder.Services.AddIyuML(_ => { });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavContext>();
        db.OrdersExt.Add(new NavOrderExt { Id = Guid.NewGuid(), Name = "first" });
        db.OrdersExt.Add(new NavOrderExt { Id = Guid.NewGuid(), Name = "second" });
        db.SecretsExt.Add(new NavSecretExt { Id = Guid.NewGuid(), Code = "c1" });
        await db.SaveChangesAsync();
        return app;
    }

    private static async Task<int> Count(WebApplication app, string dataset, ClaimsPrincipal caller)
    {
        var rows = 0;
        await foreach (var _ in app.Services.GetRequiredService<IMLDatasetSource>().ReadAsync(dataset, caller)) rows++;
        return rows;
    }

    [Fact]
    public async Task A_column_with_a_property_read_policy_cannot_be_declared_and_the_message_names_the_policy()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            o => o.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead),
            ef => ef.Dataset<NavOrderExt>("orders", d => d.Feature(x => x.Name).Feature(x => x.CreatedAt))));

        Assert.Contains("NavOrderExt.Name", refusal.Message);
        Assert.Contains(NamesRead, refusal.Message);
        Assert.DoesNotContain("NavOrderExt.CreatedAt", refusal.Message);
    }

    [Fact]
    public async Task Declaring_the_same_column_over_the_table_behind_the_view_is_no_way_around_the_policy()
    {
        // The policy is declared on the read type; the dataset reads the write type, the table under it.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            o => o.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead),
            ef => ef.Dataset<NavOrder>("orders", d => d.Feature(x => x.Name))));

        Assert.Contains("NavOrder.Name", refusal.Message);
        Assert.Contains(NamesRead, refusal.Message);
    }

    [Fact]
    public async Task A_filter_on_a_guarded_property_is_refused_too()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(
            o => o.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead),
            ef => ef.Dataset<NavOrderExt>("orders", d => d.Feature(x => x.CreatedAt).Where(x => x.Name == "first"))));

        Assert.Contains("NavOrderExt.Name", refusal.Message);
        Assert.Contains("filter", refusal.Message);
    }

    [Fact]
    public async Task Unguarded_columns_of_a_set_with_a_guarded_property_are_exported_to_a_caller_who_may_read_the_set()
    {
        await using var app = await StartAsync(
            o => o.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead),
            ef => ef.Dataset<NavOrderExt>("orders", d => d.Feature(x => x.CreatedAt).Feature(x => x.Id)));

        Assert.Equal(2, await Count(app, "orders", Caller()));
    }

    [Fact]
    public async Task A_caller_who_fails_the_entity_read_policy_gets_no_rows_and_one_who_satisfies_it_gets_them()
    {
        await using var app = await StartAsync(
            o => o.Authorize<NavOrderExt>(read: OrdersRead),
            ef => ef.Dataset<NavOrderExt>("orders", d => d.Feature(x => x.CreatedAt).Feature(x => x.Id)));

        Assert.Equal(0, await Count(app, "orders", Caller()));
        Assert.Equal(0, await Count(app, "orders", Caller(NamesRead)));
        Assert.Equal(2, await Count(app, "orders", Caller(OrdersRead)));
    }

    [Fact]
    public async Task The_same_entity_read_policy_applies_to_a_dataset_over_the_write_type()
    {
        await using var app = await StartAsync(
            o => o.Authorize<NavOrderExt>(read: OrdersRead),
            ef => ef.Dataset<NavOrder>("orders", d => d.Feature(x => x.CreatedAt).Feature(x => x.Id)));

        Assert.Equal(0, await Count(app, "orders", Caller()));
    }

    [Fact]
    public async Task A_set_without_a_read_policy_is_held_to_the_hosts_fallback_policy()
    {
        await using var app = await StartAsync(
            _ => { },
            ef => ef.Dataset<NavSecretExt>("secrets", d => d.Feature(x => x.Code)),
            o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireClaim("perm", "fallback").Build());

        Assert.Equal(0, await Count(app, "secrets", Caller()));
        Assert.Equal(1, await Count(app, "secrets", Caller("fallback")));
    }

    [Fact]
    public async Task A_host_with_no_policies_at_all_exports_to_any_signed_in_caller_and_still_refuses_an_anonymous_one()
    {
        await using var app = await StartAsync(_ => { }, ef => ef.Dataset<NavOrderExt>("orders", d => d.Feature(x => x.CreatedAt)));

        Assert.Equal(2, await Count(app, "orders", Caller()));
        Assert.Throws<UnauthorizedAccessException>(() => app.Services.GetRequiredService<IMLDatasetSource>().ReadAsync("orders", new ClaimsPrincipal()));
    }

    [Fact]
    public void A_second_guard_registration_is_refused_in_either_order()
    {
        var twice = new ServiceCollection().AddIyuMLMainServerReadGuard();
        Assert.Throws<InvalidOperationException>(() => twice.AddIyuMLMainServerReadGuard());

        var beside = new ServiceCollection();
        beside.AddScoped<IMLReadGuard, MainServerMLReadGuardRecorder>();
        Assert.Throws<InvalidOperationException>(() => beside.AddIyuMLMainServerReadGuard());
    }

    private sealed class MainServerMLReadGuardRecorder : IMLReadGuard
    {
        public string? RefusalFor(Type entity, string property) => null;

        public ValueTask<bool> MayReadAsync(Type entity, ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    [Fact]
    public async Task A_host_that_registered_the_guard_cannot_also_say_it_has_no_read_policies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddIyuMainServer<NavContext>(
            configureDb: db => db.UseInMemoryDatabase("ml-" + Guid.NewGuid().ToString("N")),
            configure: options => options.ControllerAssemblies.Add(typeof(NavOrdersController).Assembly));
        builder.Services.AddIyuMLEntityFramework<NavContext>(ef => ef.Dataset<NavOrderExt>("orders", d => d.Feature(x => x.CreatedAt)));
        builder.Services.AddIyuMLMainServerReadGuard();
        builder.Services.AddIyuML(o => o.NoReadPolicies());
        await using var app = builder.Build();
        app.UseIyuMainServer();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("choose between", refusal.Message);
    }
}
