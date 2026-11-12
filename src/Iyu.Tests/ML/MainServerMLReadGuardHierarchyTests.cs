using System.Security.Claims;
using Iyu.Core.Entities;
using Iyu.MainServer;
using Iyu.MainServer.ML;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.ML;

public class PolicyBase : IyuEntity
{
    public string Title { get; set; } = "";
}

public class PolicyServed : PolicyBase
{
    public string Extra { get; set; } = "";
}

public class PolicyServedChild : PolicyServed;

public class PolicyWrite : IyuEntity
{
    public string Title { get; set; } = "";
}

public class PolicyChildWrite : IyuEntity
{
    public string Title { get; set; } = "";
}

public class PolicyUnrelated : IyuEntity
{
    public string Title { get; set; } = "";
}

/// <summary>
/// Policies are declared on the types a host serves, but a dataset is declared on whichever type its author picks. A type
/// that reaches the same rows — a base of the served type, one derived from it — must not be a way around the policy.
/// </summary>
public class MainServerMLReadGuardHierarchyTests
{
    private const string TitleRead = "title.read";
    private const string SetRead = "set.read";

    private static MainServerMLReadGuard Guard()
    {
        var registry = new IyuEntityPairRegistry();
        registry.Register<PolicyServed, PolicyWrite>("served");
        registry.RestrictPolicy("served", SetRead, null);
        registry.RestrictProperty("served", nameof(PolicyBase.Title), TitleRead);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy(SetRead, p => p.RequireClaim("perm", SetRead)));
        var provider = services.BuildServiceProvider();
        return new MainServerMLReadGuard(
            new IyuMainServerOptions(), registry, provider.GetRequiredService<IAuthorizationService>(), provider.GetRequiredService<IAuthorizationPolicyProvider>());
    }

    private static ClaimsPrincipal Caller(params string[] permissions) =>
        new(new ClaimsIdentity([.. permissions.Select(p => new Claim("perm", p))], "test"));

    [Theory]
    [InlineData(typeof(PolicyServed))]
    [InlineData(typeof(PolicyWrite))]
    [InlineData(typeof(PolicyBase))]
    [InlineData(typeof(PolicyServedChild))]
    public void A_property_policy_follows_the_rows_to_every_type_that_reaches_them(Type entity) =>
        Assert.Contains(TitleRead, Guard().RefusalFor(entity, nameof(PolicyBase.Title)));

    [Fact]
    public void A_type_with_nothing_to_do_with_the_served_one_is_not_held_to_its_policies()
    {
        Assert.Null(Guard().RefusalFor(typeof(PolicyUnrelated), nameof(PolicyUnrelated.Title)));
        Assert.Null(Guard().RefusalFor(typeof(PolicyServed), nameof(PolicyServed.Extra)));
    }

    [Theory]
    [InlineData(typeof(PolicyServed))]
    [InlineData(typeof(PolicyBase))]
    [InlineData(typeof(PolicyServedChild))]
    public async Task The_entity_read_policy_follows_the_rows_too(Type entity)
    {
        var guard = Guard();

        Assert.False(await guard.MayReadAsync(entity, Caller()));
        Assert.True(await guard.MayReadAsync(entity, Caller(SetRead)));
    }

    [Fact]
    public async Task A_related_set_with_no_read_policy_is_held_to_the_fallback_even_when_another_related_set_names_one()
    {
        // The base type's rows are reached through two served sets: one names a read policy, the other names none.
        var registry = new IyuEntityPairRegistry();
        registry.Register<PolicyServed, PolicyWrite>("served");
        registry.RestrictPolicy("served", SetRead, null);
        registry.Register<PolicyServedChild, PolicyChildWrite>("child");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o =>
        {
            o.AddPolicy(SetRead, p => p.RequireClaim("perm", SetRead));
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireClaim("perm", "fallback").Build();
        });
        var provider = services.BuildServiceProvider();
        var guard = new MainServerMLReadGuard(
            new IyuMainServerOptions(), registry, provider.GetRequiredService<IAuthorizationService>(), provider.GetRequiredService<IAuthorizationPolicyProvider>());

        Assert.False(await guard.MayReadAsync(typeof(PolicyBase), Caller(SetRead)));
        Assert.False(await guard.MayReadAsync(typeof(PolicyBase), Caller("fallback")));
        Assert.True(await guard.MayReadAsync(typeof(PolicyBase), Caller(SetRead, "fallback")));
    }

    [Fact]
    public async Task A_type_no_set_serves_is_held_to_the_fallback_policy()
    {
        var registry = new IyuEntityPairRegistry();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireClaim("perm", "fallback").Build());
        var provider = services.BuildServiceProvider();
        var guard = new MainServerMLReadGuard(
            new IyuMainServerOptions(), registry, provider.GetRequiredService<IAuthorizationService>(), provider.GetRequiredService<IAuthorizationPolicyProvider>());

        Assert.False(await guard.MayReadAsync(typeof(PolicyUnrelated), Caller()));
        Assert.True(await guard.MayReadAsync(typeof(PolicyUnrelated), Caller("fallback")));
    }

    [Fact]
    public async Task An_unrelated_type_is_not_held_to_the_served_sets_read_policy()
    {
        Assert.True(await Guard().MayReadAsync(typeof(PolicyUnrelated), Caller()));
    }

    [Fact]
    public async Task A_type_only_an_optional_surface_serves_is_held_to_its_declared_policies()
    {
        // No OData pair: the declarations are the only place the policies exist, as for a type only GraphQL serves.
        var options = new IyuMainServerOptions()
            .Authorize<PolicyBase>(read: SetRead)
            .AuthorizeProperty<PolicyBase>(x => x.Title, TitleRead);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy(SetRead, p => p.RequireClaim("perm", SetRead)));
        var provider = services.BuildServiceProvider();
        var guard = new MainServerMLReadGuard(
            options, new IyuEntityPairRegistry(), provider.GetRequiredService<IAuthorizationService>(), provider.GetRequiredService<IAuthorizationPolicyProvider>());

        Assert.Contains(TitleRead, guard.RefusalFor(typeof(PolicyBase), nameof(PolicyBase.Title)));
        Assert.Contains(TitleRead, guard.RefusalFor(typeof(PolicyServed), nameof(PolicyBase.Title)));
        Assert.Null(guard.RefusalFor(typeof(PolicyUnrelated), nameof(PolicyUnrelated.Title)));
        Assert.False(await guard.MayReadAsync(typeof(PolicyBase), Caller()));
        Assert.True(await guard.MayReadAsync(typeof(PolicyBase), Caller(SetRead)));
        Assert.True(await guard.MayReadAsync(typeof(PolicyUnrelated), Caller()));
    }

    [Fact]
    public void A_property_policy_on_a_type_with_no_entity_policy_still_refuses_the_column()
    {
        var options = new IyuMainServerOptions().AuthorizeProperty<PolicyBase>(x => x.Title, TitleRead);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        var provider = services.BuildServiceProvider();
        var guard = new MainServerMLReadGuard(
            options, new IyuEntityPairRegistry(), provider.GetRequiredService<IAuthorizationService>(), provider.GetRequiredService<IAuthorizationPolicyProvider>());

        Assert.Contains(TitleRead, guard.RefusalFor(typeof(PolicyBase), nameof(PolicyBase.Title)));
    }

    /// <summary>A surface that serves one pair on its own, as the GraphQL surface can for a host that does not serve OData.</summary>
    public sealed class PairOnlySurface : IIyuMainServerSurface
    {
        public bool Serves(Type readType) => readType == typeof(PolicyServed);

        public IReadOnlyCollection<(Type ReadType, Type WriteType)> WriteTypes { get; } = [(typeof(PolicyServed), typeof(PolicyWrite))];

        public void ConfigureServices(IServiceCollection services, IyuMainServerOptions options)
        {
        }

        public bool EnforcesPropertyPolicies => true;

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }

    [Fact]
    public async Task A_dataset_over_the_table_behind_a_pair_only_an_optional_surface_serves_finds_the_read_types_policies()
    {
        // No OData pair at all: the only link from the read type to the table behind it is the surface's own pair list.
        var options = new IyuMainServerOptions()
            .Authorize<PolicyServed>(read: SetRead)
            .AuthorizeProperty<PolicyServed>(x => x.Extra, TitleRead);
        options.Surface<PairOnlySurface>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy(SetRead, p => p.RequireClaim("perm", SetRead)));
        var provider = services.BuildServiceProvider();
        var guard = new MainServerMLReadGuard(
            options, new IyuEntityPairRegistry(), provider.GetRequiredService<IAuthorizationService>(), provider.GetRequiredService<IAuthorizationPolicyProvider>());

        Assert.False(await guard.MayReadAsync(typeof(PolicyWrite), Caller()));
        Assert.True(await guard.MayReadAsync(typeof(PolicyWrite), Caller(SetRead)));
        Assert.Contains(TitleRead, guard.RefusalFor(typeof(PolicyWrite), nameof(PolicyServed.Extra)));
        Assert.True(await guard.MayReadAsync(typeof(PolicyUnrelated), Caller()));
    }
}
