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
