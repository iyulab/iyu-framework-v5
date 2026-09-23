using System.Linq;
using HotChocolate;
using HotChocolate.Execution;
using System.Security.Claims;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.GraphQL;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

/// <summary>
/// The GraphQL counterpart of the question the OData surface already answered: a policy attached to
/// one query field, and a navigation property on a type reachable from another field.
/// <para>
/// <c>IyuGraphQLSchemaBuilder</c> attaches the policy with <c>field.Authorize(policy)</c> on the
/// <em>root query field</em>, not on the object type. That is the same shape that let an
/// <c>$expand</c> walk past an OData set's read policy, so the question is whether a GraphQL
/// selection can walk past it the same way.
/// </para>
/// <para>
/// These tests measure both halves separately on purpose. "The protected value did not appear" is
/// only evidence about authorization if the value <em>could</em> have appeared — and this
/// repository has already been bitten once by a gate that passed because the thing it was watching
/// was absent for an unrelated reason.
/// </para>
/// </summary>
public class GraphQLProjectionAuthorizationTests
{
    public sealed class ProjSecret : IyuEntity
    {
        public string Code { get; set; } = "";
    }

    public sealed class ProjOrder : IyuEntity
    {
        public string Name { get; set; } = "";
        public Guid? SecretId { get; set; }
        public ProjSecret? Secret { get; set; }
    }

    public sealed class ProjContext(DbContextOptions<ProjContext> options) : IyuDbContext(options)
    {
        public DbSet<ProjOrder> Orders => Set<ProjOrder>();
        public DbSet<ProjSecret> Secrets => Set<ProjSecret>();
    }

    private const string SecretPolicy = "secrets.read";
    private const string SecretCode = "PROJECTION-MUST-NOT-LEAK-THIS";
    private static readonly Guid SecretId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<ServiceProvider> BuildAsync(string dbName)
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        // Orders is open; Secrets carries the policy. The caller is meant to be allowed to read
        // Orders, so anything it learns about a Secret it learned through the Order.
        graphql.AddEntityPair<ProjOrder, ProjOrder>("projOrders", "projOrder");
        graphql.AddEntityPair<ProjSecret, ProjSecret>("projSecrets", "projSecret", authorizePolicy: SecretPolicy);

        var services = new ServiceCollection();
        services.AddDbContext<ProjContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<ProjContext>());
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization(opts =>
            opts.AddPolicy(SecretPolicy, p => p.RequireClaim("perm", SecretPolicy)));

        var gql = services.AddGraphQLServer()
            .DisableIntrospection(disable: false)
            .ModifyRequestOptions(o => o.IncludeExceptionDetails = true);
        graphql.ApplyTo(gql);

        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ProjContext>();
            ctx.Secrets.Add(new ProjSecret { Id = SecretId, Code = SecretCode });
            ctx.Orders.Add(new ProjOrder { Id = Guid.NewGuid(), Name = "an order", SecretId = SecretId });
            await ctx.SaveChangesAsync();
        }

        return sp;
    }

    /// <summary>
    /// Must be called from the test method's own body, never from inside an <c>async</c> helper.
    /// <see cref="IHttpContextAccessor"/> keeps the context in an <see cref="AsyncLocal{T}"/>, and a
    /// write inside an async method belongs to that method's execution context — it does not flow
    /// back out to its caller. Setting it in the builder made every caller anonymous, and the first
    /// shape of these tests read that as "the protected value did not leak" when what it actually
    /// measured was "nobody was authorized to see anything". The contrast query that caught it —
    /// the protected field's own route refusing a caller that held its claim — is why the pair of
    /// measurements exists rather than the guard alone.
    /// </summary>
    private static void SetCurrentUser(IServiceProvider services, params (string Type, string Value)[] claims)
    {
        var identity = claims.Length == 0
            ? new ClaimsIdentity()
            : new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Test");
        services.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    /// <summary>
    /// Half one: the navigation is in the schema. HotChocolate infers an object field from the
    /// reference property, exactly as OData's convention builder infers a navigation — so the
    /// selection path a bypass would use exists the moment a generator emits such a property.
    /// </summary>
    [Fact]
    public async Task A_read_types_navigation_appears_in_the_schema()
    {
        await using var sp = await BuildAsync(nameof(A_read_types_navigation_appears_in_the_schema));
        SetCurrentUser(sp, ("perm", SecretPolicy));
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var json = (await executor.ExecuteAsync(
            "{ __type(name: \"ProjOrder\") { fields { name } } }")).ToJson();

        Assert.Contains("\"secret\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Half two, and the one that decides whether half three means anything: what a selection
    /// through that field actually returns for a caller who holds the target's policy. The
    /// resolver hands HotChocolate a bare <c>IQueryable</c> with no projection middleware and no
    /// <c>Include</c>, so this is where it becomes visible whether the navigation is populated at
    /// all — and therefore whether an unauthorized caller could have received anything.
    /// </summary>
    [Fact]
    public async Task What_an_authorized_caller_receives_through_the_navigation_is_recorded_here()
    {
        await using var sp = await BuildAsync(
            nameof(What_an_authorized_caller_receives_through_the_navigation_is_recorded_here));
        SetCurrentUser(sp, ("perm", SecretPolicy));
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var json = (await executor.ExecuteAsync("{ projOrders { nodes { name secret { code } } } }")).ToJson();

        // The contrast that validates the harness itself: this caller really is authorized, so an
        // absent value below is about the navigation, not about the caller.
        var rootJson = (await executor.ExecuteAsync("{ projSecrets { nodes { code } } }")).ToJson();
        Assert.Contains(SecretCode, rootJson, StringComparison.Ordinal);
        // Whether the value is there is the measurement. Asserted as null, because that is what the
        // resolver's bare IQueryable produces with no projection or include configured: nothing
        // loads the related row. If this ever fails, GraphQL navigation has started to resolve and
        // the guard the next test pins must be re-verified against a payload that can carry data.
        Assert.Contains("\"secret\":null", new string(json.Where(c => !char.IsWhiteSpace(c)).ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// Half three: the guard, asserted so that it is attributable to the guard. A caller without
    /// the target's policy must not receive its rows through a navigation — but today the value
    /// would be absent anyway, for the reason the previous test records, so "no leak" alone would
    /// be a green that proves nothing. What makes it evidence is <em>where the refusal appears</em>:
    /// the policy is refused at the navigation field's own path, which only happens because the
    /// policy is attached to the object type rather than to the root query field alone. Sabotage
    /// the type attachment and this assertion is what fails.
    /// </summary>
    [Fact]
    public async Task A_selection_through_a_navigation_is_refused_at_the_navigation_itself()
    {
        await using var sp = await BuildAsync(
            nameof(A_selection_through_a_navigation_is_refused_at_the_navigation_itself));
        SetCurrentUser(sp);   // anonymous
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var json = (await executor.ExecuteAsync("{ projOrders { nodes { name secret { code } } } }")).ToJson();
        // No escape sequences written through a generating script here, on purpose: that is how a
        // raw control character ends up inside a string literal. Dropping every whitespace
        // character is also what this comparison actually wants.
        var dense = new string(json.Where(c => !char.IsWhiteSpace(c)).ToArray());

        Assert.DoesNotContain(SecretCode, json, StringComparison.Ordinal);
        // The refusal is raised for the `secret` field, not for the query as a whole.
        Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", json, StringComparison.Ordinal);
        Assert.Contains("\"projOrders\",\"nodes\",0,\"secret\"", dense, StringComparison.Ordinal);
        // And the open field the caller is entitled to still answers — the guard narrows to the
        // protected type instead of failing the whole selection.
        Assert.Contains("\"name\":\"anorder\"", dense, StringComparison.Ordinal);
    }

    /// <summary>The contrast: the protected field's own route refuses the same caller.</summary>
    [Fact]
    public async Task The_protected_field_refuses_the_same_caller_directly()
    {
        await using var sp = await BuildAsync(nameof(The_protected_field_refuses_the_same_caller_directly));
        SetCurrentUser(sp);   // anonymous
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var json = (await executor.ExecuteAsync("{ projSecrets { nodes { code } } }")).ToJson();

        Assert.Contains("\"errors\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretCode, json, StringComparison.Ordinal);
    }
}
