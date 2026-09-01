using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HotChocolate;
using HotChocolate.Execution;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.GraphQL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

public class IyuGraphQLSchemaBuilderTests
{
    public sealed class Widget : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    public sealed class Annotated : IyuEntity
    {
        [Display(Description = "The bank's public display name")]
        public string BankName { get; set; } = "";

        // No [Display] — must not gain a description.
        public string AccountNumber { get; set; } = "";
    }

    public sealed class TestContext(DbContextOptions<TestContext> options) : IyuDbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
        public DbSet<Secretive> Secretives => Set<Secretive>();
        public DbSet<Annotated> Annotateds => Set<Annotated>();
    }

    private static ServiceProvider BuildServices(
        string dbName, IyuGraphQLSchemaBuilder graphql, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<TestContext>());
        configureServices?.Invoke(services);

        var gql = services.AddGraphQLServer()
            // HotChocolate 16 blocks __type/__schema by default when AddGraphQLServer()
            // resolves to the HotChocolate.AspNetCore overload (as it does here, via
            // Iyu.Server.GraphQL's package reference) outside a detected Development
            // environment — this bare ServiceCollection has no IHostEnvironment for it to
            // detect, so it fails closed. Several of this file's tests query __type for
            // schema-shape assertions — turn it back on at the schema-builder level for
            // this test-only provider (production wiring in MainServerExtensions is a
            // separate decision, not exercised by these tests).
            .DisableIntrospection(disable: false)
            .ModifyRequestOptions(o => o.IncludeExceptionDetails = true);
        graphql.ApplyTo(gql);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Stands in for what an ASP.NET Core request pipeline would otherwise populate:
    /// <see cref="IHttpContextAccessor.HttpContext"/> with a <see cref="ClaimsPrincipal"/> bearing
    /// <paramref name="claims"/>. Passing none leaves the accessor's <c>HttpContext</c> null,
    /// which the GraphQL authorization bridge treats as an anonymous user — the same posture a
    /// real anonymous request has.
    /// </summary>
    private static void SetCurrentUser(IServiceProvider services, params (string Type, string Value)[] claims)
    {
        var identity = claims.Length == 0
            ? new ClaimsIdentity()
            : new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Test");
        services.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Fact]
    public async Task Registered_pair_exposes_query_field_and_returns_rows()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");

        await using var sp = BuildServices(nameof(Registered_pair_exposes_query_field_and_returns_rows), graphql);

        // Seed
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "alpha" });
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "beta" });
            await ctx.SaveChangesAsync();
        }

        var executor = await sp
            .GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var result = await executor.ExecuteAsync("{ widgets { name } }");
        var json = result.ToJson();

        Assert.Contains("alpha", json);
        Assert.Contains("beta", json);
        Assert.DoesNotContain("\"errors\"", json);
    }


    public sealed class Secretive : IyuEntity
    {
        public string Name { get; set; } = "";
        public string SecretHash { get; set; } = "";
    }

    /// <summary>
    /// A value that is stored but must never leave the server has to be absent from the
    /// schema — not merely empty. An empty value is indistinguishable from "no value"
    /// and still lets a caller ask for it.
    /// </summary>
    [Fact]
    public async Task Excluded_property_is_absent_from_the_schema()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Secretive, Secretive>("secretives", "secretive");
        graphql.Exclude<Secretive>(x => x.SecretHash);

        await using var sp = BuildServices(nameof(Excluded_property_is_absent_from_the_schema), graphql);
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Secretives.Add(new Secretive { Id = Guid.NewGuid(), Name = "alpha", SecretHash = "MUST-NOT-LEAK" });
            await ctx.SaveChangesAsync();
        }

        var executor = await sp
            .GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        // The remaining field still works...
        var kept = (await executor.ExecuteAsync("{ secretives { name } }")).ToJson();
        Assert.Contains("alpha", kept);
        Assert.DoesNotContain("MUST-NOT-LEAK", kept);

        // ...and asking for the excluded one is a schema error, not an empty value.
        var probed = (await executor.ExecuteAsync("{ secretives { secretHash } }")).ToJson();
        Assert.Contains("\"errors\"", probed);
        Assert.DoesNotContain("MUST-NOT-LEAK", probed);
    }

    /// <summary>A nested access would silently exclude nothing — reject it where it is written.</summary>
    [Fact]
    public void Exclude_rejects_a_non_property_expression()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        Assert.Throws<ArgumentException>(() => graphql.Exclude<Secretive>(x => x.Name.Length.ToString()));
    }

    /// <summary>
    /// A type extension whose target type no field returns is discarded during schema
    /// construction — so an exclusion naming an unexposed type is a silent no-op unless it
    /// is refused. Silence is the worst outcome for a feature whose job is keeping a stored
    /// value off the wire: the caller believes the value is hidden and every query still
    /// returns it.
    /// </summary>
    [Fact]
    public void Exclude_rejects_a_type_the_schema_does_not_expose()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");
        graphql.Exclude<Secretive>(x => x.SecretHash);   // never registered as a query field type

        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(
            () => graphql.ApplyTo(services.AddGraphQLServer()));

        Assert.Contains(nameof(Secretive), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Widget), error.Message, StringComparison.Ordinal);   // what *is* exposed
    }

    /// <summary>Order-independence: nothing is applied until <c>ApplyTo</c>.</summary>
    [Fact]
    public async Task Exclude_applies_even_though_it_was_called_before_the_pair_was_registered()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.Exclude<Secretive>(x => x.SecretHash);
        graphql.AddEntityPair<Secretive, Secretive>("secretives", "secretive");

        await using var sp = BuildServices(
            nameof(Exclude_applies_even_though_it_was_called_before_the_pair_was_registered), graphql);
        var executor = await sp
            .GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var probed = (await executor.ExecuteAsync("{ secretives { secretHash } }")).ToJson();
        Assert.Contains("\"errors\"", probed);
    }

    [Fact]
    public void Duplicate_query_name_throws()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");
        Assert.Throws<InvalidOperationException>(
            () => graphql.AddEntityPair<Widget, Widget>("widgets", "widget"));
    }

    [Fact]
    public void GetMutationPrefix_returns_registered_value()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");
        Assert.Equal("widget", graphql.GetMutationPrefix("widgets"));
        Assert.Null(graphql.GetMutationPrefix("unknown"));
    }

    /// <summary>
    /// A generated entity's <c>[Display(Description = "...")]</c> reaches GraphQL clients as
    /// the field's standard <c>description</c> — the same text a generated form already shows,
    /// now visible in schema introspection too.
    /// </summary>
    [Fact]
    public async Task Display_description_becomes_the_graphql_field_description()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Annotated, Annotated>("annotateds", "annotated");

        await using var sp = BuildServices(nameof(Display_description_becomes_the_graphql_field_description), graphql);
        var executor = await sp
            .GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var result = await executor.ExecuteAsync(
            "{ __type(name: \"Annotated\") { fields { name description } } }");
        var json = result.ToJson();

        Assert.Contains("\"name\": \"bankName\"", json);
        Assert.Contains("The bank's public display name", json);

        // accountNumber has no [Display] — its description must stay null.
        var accountField = System.Text.Json.JsonDocument.Parse(json)
            .RootElement.GetProperty("data").GetProperty("__type").GetProperty("fields")
            .EnumerateArray().Single(f => f.GetProperty("name").GetString() == "accountNumber");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, accountField.GetProperty("description").ValueKind);
    }

    private static void ConfigurePermissionPolicy(IServiceCollection services, string policyName, string claimValue)
    {
        // DefaultAuthorizationService (registered by AddAuthorization) needs ILogger<T> — present
        // for free in a real host (WebApplication.CreateBuilder), missing on this bare
        // ServiceCollection unless added explicitly.
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization(opts =>
            opts.AddPolicy(policyName, p => p.RequireClaim("perm", claimValue)));
    }

    /// <summary>
    /// docket #139: a query field registered with <c>authorizePolicy</c> must reject a caller
    /// who lacks the required claim — this is the exact gap the issue reported (GraphQL had no
    /// per-entity authorization while OData already did for the same data).
    /// </summary>
    [Fact]
    public async Task Field_with_authorize_policy_rejects_a_caller_without_the_claim()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget", authorizePolicy: "widgets.read");

        await using var sp = BuildServices(
            nameof(Field_with_authorize_policy_rejects_a_caller_without_the_claim), graphql,
            services => ConfigurePermissionPolicy(services, "widgets.read", "widgets.read"));
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "SHOULD-NOT-LEAK" });
            await ctx.SaveChangesAsync();
        }
        // No claim at all — the same posture a bearer token missing the permission has.
        SetCurrentUser(sp);

        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var result = await executor.ExecuteAsync("{ widgets { name } }");
        var json = result.ToJson();

        Assert.Contains("\"errors\"", json);
        Assert.DoesNotContain("SHOULD-NOT-LEAK", json);
    }

    /// <summary>The same field, same policy, with the required claim present — must succeed.</summary>
    [Fact]
    public async Task Field_with_authorize_policy_allows_a_caller_with_the_claim()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget", authorizePolicy: "widgets.read");

        await using var sp = BuildServices(
            nameof(Field_with_authorize_policy_allows_a_caller_with_the_claim), graphql,
            services => ConfigurePermissionPolicy(services, "widgets.read", "widgets.read"));
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "alpha" });
            await ctx.SaveChangesAsync();
        }
        SetCurrentUser(sp, ("perm", "widgets.read"));

        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var result = await executor.ExecuteAsync("{ widgets { name } }");
        var json = result.ToJson();

        Assert.Contains("alpha", json);
        Assert.DoesNotContain("\"errors\"", json);
    }

    /// <summary>
    /// A typo'd or not-yet-registered policy name must fail closed (a clean GraphQL error), not
    /// throw an unhandled exception from inside <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/> —
    /// the bridge resolves the policy itself first precisely so this case maps to HotChocolate's
    /// own <c>PolicyNotFound</c> outcome instead.
    /// </summary>
    [Fact]
    public async Task Field_with_an_unregistered_authorize_policy_fails_closed()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget", authorizePolicy: "widgets.read");

        await using var sp = BuildServices(
            nameof(Field_with_an_unregistered_authorize_policy_fails_closed), graphql,
            services =>
            {
                services.AddLogging();
                services.AddHttpContextAccessor();
                services.AddAuthorization(); // no "widgets.read" policy registered
            });
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "SHOULD-NOT-LEAK" });
            await ctx.SaveChangesAsync();
        }
        SetCurrentUser(sp, ("perm", "widgets.read"));

        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var result = await executor.ExecuteAsync("{ widgets { name } }");
        var json = result.ToJson();

        Assert.Contains("\"errors\"", json);
        Assert.DoesNotContain("SHOULD-NOT-LEAK", json);
        Assert.DoesNotContain("Unexpected Execution Error", json);
    }

    /// <summary>
    /// Backward compatibility: a pair registered without <c>authorizePolicy</c> (every call site
    /// before this parameter existed) must not gain a claim requirement it never asked for — no
    /// authorization handler is even wired unless some pair opts in.
    /// </summary>
    [Fact]
    public async Task Field_without_authorize_policy_is_reachable_by_an_anonymous_caller()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");

        await using var sp = BuildServices(
            nameof(Field_without_authorize_policy_is_reachable_by_an_anonymous_caller), graphql);
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "alpha" });
            await ctx.SaveChangesAsync();
        }

        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var result = await executor.ExecuteAsync("{ widgets { name } }");
        var json = result.ToJson();

        Assert.Contains("alpha", json);
        Assert.DoesNotContain("\"errors\"", json);
    }

    /// <summary>
    /// A consumer whose registration is code-generated cannot pass <c>authorizePolicy</c> at the
    /// <see cref="IyuGraphQLSchemaBuilder.AddEntityPair{TRead,TWrite}"/> call site it does not
    /// own — <see cref="IyuGraphQLSchemaBuilder.Restrict"/> lets it apply the policy afterward,
    /// from its own composition root, the same shape <c>IyuEdmModelBuilder.Restrict</c> already
    /// gives the OData surface.
    /// </summary>
    [Fact]
    public async Task Restrict_after_plain_registration_rejects_a_caller_without_the_claim()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget"); // no authorizePolicy — as codegen emits it
        graphql.Restrict("widgets", "widgets.read");                // applied from a separate call site

        await using var sp = BuildServices(
            nameof(Restrict_after_plain_registration_rejects_a_caller_without_the_claim), graphql,
            services => ConfigurePermissionPolicy(services, "widgets.read", "widgets.read"));
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "SHOULD-NOT-LEAK" });
            await ctx.SaveChangesAsync();
        }
        SetCurrentUser(sp); // no claim

        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var result = await executor.ExecuteAsync("{ widgets { name } }");
        var json = result.ToJson();

        Assert.Contains("\"errors\"", json);
        Assert.DoesNotContain("SHOULD-NOT-LEAK", json);
    }

    /// <summary>The same field, same policy applied via <c>Restrict</c>, with the required claim present.</summary>
    [Fact]
    public async Task Restrict_after_plain_registration_allows_a_caller_with_the_claim()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");
        graphql.Restrict("widgets", "widgets.read");

        await using var sp = BuildServices(
            nameof(Restrict_after_plain_registration_allows_a_caller_with_the_claim), graphql,
            services => ConfigurePermissionPolicy(services, "widgets.read", "widgets.read"));
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Widgets.Add(new Widget { Id = Guid.NewGuid(), Name = "alpha" });
            await ctx.SaveChangesAsync();
        }
        SetCurrentUser(sp, ("perm", "widgets.read"));

        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var result = await executor.ExecuteAsync("{ widgets { name } }");
        var json = result.ToJson();

        Assert.Contains("alpha", json);
        Assert.DoesNotContain("\"errors\"", json);
    }

    /// <summary>Order-independence within registration: Restrict may run before AddEntityPair too.</summary>
    [Fact]
    public void Restrict_before_add_entity_pair_throws_because_the_field_is_not_registered_yet()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        var error = Assert.Throws<InvalidOperationException>(() => graphql.Restrict("widgets", "widgets.read"));
        Assert.Contains("widgets", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Naming a query field that was never registered is refused — the same fail-loud posture
    /// <see cref="IyuGraphQLSchemaBuilder.Exclude{T}"/> and <c>IyuEntityPairRegistry.Restrict</c>
    /// (Iyu.Server.OData) already take, instead of silently doing nothing.
    /// </summary>
    [Fact]
    public void Restrict_on_an_unregistered_query_name_throws()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");
        var error = Assert.Throws<InvalidOperationException>(() => graphql.Restrict("unknown", "some.policy"));
        Assert.Contains("unknown", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unlike <c>IyuEdmModelBuilder.Restrict</c>, this one has a real ordering constraint:
    /// <see cref="IyuGraphQLSchemaBuilder.ApplyTo"/> decides synchronously, during service
    /// configuration, whether to wire the authorization handler into DI — a policy applied after
    /// that decision was made would silently never be enforced, so it is refused instead.
    /// </summary>
    [Fact]
    public void Restrict_after_apply_to_throws()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Widget, Widget>("widgets", "widget");

        var services = new ServiceCollection();
        graphql.ApplyTo(services.AddGraphQLServer());

        var error = Assert.Throws<InvalidOperationException>(() => graphql.Restrict("widgets", "widgets.read"));
        Assert.Contains("ApplyTo", error.Message, StringComparison.Ordinal);
    }
}
