using System.Text.Json;
using HotChocolate;
using HotChocolate.Execution;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

/// <summary>
/// A list of primitive values on a served read type is a plain list field — its values, not a navigation to page —
/// and survives the projection the query field applies.
/// </summary>
/// <remarks>
/// Collection navigations are read through their own paged query; only a collection of keyed models is one. A
/// <c>List&lt;string&gt;</c> must not be taken for a navigation, or it would be dropped from the projection or turned into
/// a connection with nothing to page.
/// </remarks>
public sealed class GraphQLPrimitiveCollectionTests : IDisposable
{
    public sealed class Label : IyuEntity
    {
        public string Name { get; set; } = "";
        public List<string> Tags { get; set; } = [];
        public List<int>? Scores { get; set; }
    }

    public sealed class LabelContext(DbContextOptions<LabelContext> options) : IyuDbContext(options)
    {
        public DbSet<Label> Labels => Set<Label>();
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public GraphQLPrimitiveCollectionTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private async Task<IRequestExecutor> BuildAsync()
    {
        var graphql = new IyuGraphQLSchemaBuilder();
        graphql.AddEntityPair<Label, Label>("labels", "label");

        var services = new ServiceCollection();
        services.AddDbContext<LabelContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<LabelContext>());
        services.AddLogging();
        graphql.ApplyTo(services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<LabelContext>();
            await ctx.Database.EnsureCreatedAsync();
            ctx.Labels.Add(new Label { Name = "warm", Tags = ["red", "orange"], Scores = [3, 1] });
            ctx.Labels.Add(new Label { Name = "plain" });
            await ctx.SaveChangesAsync();
        }
        return await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
    }

    [Fact]
    public async Task The_schema_types_the_list_as_a_list_of_its_element()
    {
        var schema = (await BuildAsync()).Schema.ToString();

        Assert.Contains("tags: [String!]!", schema, StringComparison.Ordinal);
        Assert.Contains("scores: [Int!]", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_reads_the_list_values()
    {
        var executor = await BuildAsync();

        var json = (await executor.ExecuteAsync("{ labels { nodes { name tags scores } } }")).ToJson();

        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        var nodes = root.GetProperty("data").GetProperty("labels").GetProperty("nodes").EnumerateArray().ToList();
        var warm = nodes.Single(n => n.GetProperty("name").GetString() == "warm");
        Assert.Equal(["red", "orange"], warm.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal([3, 1], warm.GetProperty("scores").EnumerateArray().Select(s => s.GetInt32()));
        var plain = nodes.Single(n => n.GetProperty("name").GetString() == "plain");
        Assert.Equal(0, plain.GetProperty("tags").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("scores").ValueKind);
    }
}
