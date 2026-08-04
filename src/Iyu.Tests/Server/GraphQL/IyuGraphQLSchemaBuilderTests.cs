using HotChocolate;
using HotChocolate.Execution;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.GraphQL;
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

    public sealed class TestContext(DbContextOptions<TestContext> options) : IyuDbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
        public DbSet<Secretive> Secretives => Set<Secretive>();
    }

    private static ServiceProvider BuildServices(string dbName, IyuGraphQLSchemaBuilder graphql)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<TestContext>());

        var gql = services.AddGraphQLServer();
        graphql.ApplyTo(gql);

        return services.BuildServiceProvider();
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

        var executor = await sp.GetRequiredService<IRequestExecutorResolver>()
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

        var executor = await sp.GetRequiredService<IRequestExecutorResolver>()
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
}
