using HotChocolate;
using HotChocolate.Execution;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.GraphQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

/// <summary>
/// A read type whose navigation points at an entity no query field exposes. HotChocolate would infer
/// both the field and an object type for the target, so the schema would describe a model the
/// application keeps off its API. The schema keeps only navigations to exposed types — the same
/// boundary the OData model draws.
/// </summary>
public class GraphQLUnexposedNavigationTests
{
    public sealed class KeptOffApi : IyuEntity
    {
        public string Secret { get; set; } = "";
    }

    public sealed class ExposedTarget : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    public sealed class ExposedHolder : IyuEntity
    {
        public string Title { get; set; } = "";
        public KeptOffApi? Hidden { get; set; }
        public List<KeptOffApi> HiddenMany { get; set; } = [];
        public ExposedTarget? Target { get; set; }
    }

    public sealed class HolderContext(DbContextOptions<HolderContext> options) : IyuDbContext(options)
    {
        public DbSet<ExposedHolder> Holders => Set<ExposedHolder>();
        public DbSet<ExposedTarget> Targets => Set<ExposedTarget>();
        public DbSet<KeptOffApi> Kept => Set<KeptOffApi>();
    }

    private static async Task<IRequestExecutor> BuildAsync()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<ExposedHolder, ExposedHolder>("holders", "holder");
        // Registered after the holder on purpose: exposure is decided once every pair is known.
        graphql.AddEntityPair<ExposedTarget, ExposedTarget>("targets", "target");

        var services = new ServiceCollection();
        services.AddDbContext<HolderContext>(o => o.UseInMemoryDatabase(nameof(GraphQLUnexposedNavigationTests)));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<HolderContext>());
        services.AddLogging();
        var gql = services.AddGraphQLServer().DisableIntrospection(disable: false);
        graphql.ApplyTo(gql);
        return await services.BuildServiceProvider().GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
    }

    [Fact]
    public async Task A_navigation_to_an_unexposed_type_is_not_in_the_schema_and_neither_is_the_type()
    {
        var executor = await BuildAsync();

        var fields = (await executor.ExecuteAsync("{ __type(name: \"ExposedHolder\") { fields { name } } }")).ToJson();
        Assert.Contains("\"title\"", fields, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hidden\"", fields, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hiddenMany\"", fields, StringComparison.Ordinal);

        var type = (await executor.ExecuteAsync("{ __type(name: \"KeptOffApi\") { name } }")).ToJson();
        Assert.Contains("\"__type\": null", type, StringComparison.Ordinal);
    }

    /// <summary>The contrast: a navigation to an exposed type stays, so the rule is not "drop all".</summary>
    [Fact]
    public async Task A_navigation_to_an_exposed_type_stays()
    {
        var executor = await BuildAsync();

        var fields = (await executor.ExecuteAsync("{ __type(name: \"ExposedHolder\") { fields { name } } }")).ToJson();
        Assert.Contains("\"target\"", fields, StringComparison.Ordinal);
    }
}
