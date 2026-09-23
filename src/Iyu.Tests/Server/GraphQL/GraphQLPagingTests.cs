using System.Text.Json;
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
/// A query field is a cursor connection bounded by <see cref="IyuGraphQLSchemaBuilder.MaxPageSize"/>:
/// no request receives a whole table, and a client walks the set with <c>after</c>.
/// </summary>
public class GraphQLPagingTests
{
    public sealed class Item : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    public sealed class PagingContext(DbContextOptions<PagingContext> options) : IyuDbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
    }

    private const int RowCount = 5;

    private static async Task<IRequestExecutor> BuildAsync(
        string dbName, int maxPageSize, int defaultPageSize, Func<int, Guid>? idOf = null)
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = maxPageSize, DefaultPageSize = defaultPageSize };
        graphql.AddEntityPair<Item, Item>("items", "item");

        var services = new ServiceCollection();
        services.AddDbContext<PagingContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<PagingContext>());
        graphql.ApplyTo(services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<PagingContext>();
            for (var i = 0; i < RowCount; i++)
                ctx.Items.Add(new Item { Id = idOf?.Invoke(i) ?? Guid.NewGuid(), Name = $"item-{i}" });
            await ctx.SaveChangesAsync();
        }
        return await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
    }

    private static JsonElement Data(string json, string field) =>
        JsonDocument.Parse(json).RootElement.GetProperty("data").GetProperty(field);

    [Fact]
    public async Task A_request_that_names_no_page_size_gets_the_default_page_and_is_told_there_is_more()
    {
        var executor = await BuildAsync(nameof(A_request_that_names_no_page_size_gets_the_default_page_and_is_told_there_is_more), 3, 2);

        var json = (await executor.ExecuteAsync("{ items { nodes { name } pageInfo { hasNextPage } } }")).ToJson();

        var items = Data(json, "items");
        Assert.Equal(2, items.GetProperty("nodes").GetArrayLength());
        Assert.True(items.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
    }

    [Fact]
    public async Task A_page_larger_than_the_maximum_is_refused()
    {
        var executor = await BuildAsync(nameof(A_page_larger_than_the_maximum_is_refused), 3, 2);

        var json = (await executor.ExecuteAsync("{ items(first: 4) { nodes { name } } }")).ToJson();

        Assert.Contains("\"errors\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("item-", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walking the pages returns every row exactly once — which holds only if the pages are slices
    /// of one ordering.
    /// </summary>
    [Fact]
    public async Task Walking_the_pages_returns_every_row_exactly_once()
    {
        var executor = await BuildAsync(nameof(Walking_the_pages_returns_every_row_exactly_once), 3, 2);

        var seen = new List<string>();
        string? after = null;
        for (var guard = 0; guard < RowCount + 1; guard++)
        {
            var args = after is null ? "first: 2" : $"first: 2, after: \"{after}\"";
            var json = (await executor.ExecuteAsync($"{{ items({args}) {{ nodes {{ name }} pageInfo {{ hasNextPage endCursor }} }} }}")).ToJson();
            var items = Data(json, "items");
            seen.AddRange(items.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("name").GetString()!));
            var pageInfo = items.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean()) break;
            after = pageInfo.GetProperty("endCursor").GetString();
        }

        Assert.Equal(RowCount, seen.Count);
        Assert.Equal(RowCount, seen.Distinct().Count());
    }

    /// <summary>
    /// The pages are slices of the key order, not of whatever order the store returns — rows are
    /// inserted with descending keys, so the first page must hold the two smallest.
    /// </summary>
    [Fact]
    public async Task Pages_follow_the_key_order_not_the_insertion_order()
    {
        var executor = await BuildAsync(
            nameof(Pages_follow_the_key_order_not_the_insertion_order), 3, 2,
            i => new Guid($"00000000-0000-0000-0000-00000000000{RowCount - i}"));

        var json = (await executor.ExecuteAsync("{ items(first: 2) { nodes { name } } }")).ToJson();

        var names = Data(json, "items").GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { $"item-{RowCount - 1}", $"item-{RowCount - 2}" }, names);
    }

    [Fact]
    public void A_default_page_larger_than_the_maximum_is_rejected_when_the_schema_is_built()
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = 3, DefaultPageSize = 4 };
        graphql.AddEntityPair<Item, Item>("items", "item");

        Assert.Throws<InvalidOperationException>(() => graphql.ApplyTo(new ServiceCollection().AddGraphQLServer()));
    }
}
