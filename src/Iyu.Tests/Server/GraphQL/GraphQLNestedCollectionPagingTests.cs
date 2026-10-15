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
/// A collection navigation on a served read type is a connection bounded like a query field: a parent page does not
/// carry every child row. Without it a request bounded at the root still receives one page of parents times all of
/// each parent's children — the unbounded second door the OData surface closes with the same page size.
/// </summary>
public sealed class GraphQLNestedCollectionPagingTests : IDisposable
{
    public sealed class Shelf : IyuEntity
    {
        public string Name { get; set; } = "";
        public List<Book> Books { get; set; } = [];
    }

    public sealed class Book : IyuEntity
    {
        public string Title { get; set; } = "";
        public Guid ShelfId { get; set; }
        public Shelf? Shelf { get; set; }
    }

    public sealed class ShelfContext(DbContextOptions<ShelfContext> options) : IyuDbContext(options)
    {
        public DbSet<Shelf> Shelves => Set<Shelf>();
        public DbSet<Book> Books => Set<Book>();
    }

    private const int BookCount = 5;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public GraphQLNestedCollectionPagingTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private async Task<IRequestExecutor> BuildAsync(int maxPageSize, int defaultPageSize)
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = maxPageSize, DefaultPageSize = defaultPageSize };
        graphql.AddEntityPair<Shelf, Shelf>("shelves", "shelf");
        graphql.AddEntityPair<Book, Book>("books", "book");

        var services = new ServiceCollection();
        services.AddDbContext<ShelfContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<ShelfContext>());
        services.AddLogging();
        graphql.ApplyTo(services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ShelfContext>();
            await ctx.Database.EnsureCreatedAsync();
            var shelf = new Shelf { Name = "fiction" };
            for (var i = 0; i < BookCount; i++) shelf.Books.Add(new Book { Title = $"book-{i}" });
            ctx.Shelves.Add(shelf);
            ctx.Shelves.Add(new Shelf { Name = "empty" });
            await ctx.SaveChangesAsync();
        }
        return await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
    }

    private static JsonElement ShelfNode(string json, string name)
    {
        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data").GetProperty("shelves").GetProperty("nodes").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task A_collection_that_names_no_page_size_gets_the_default_page_and_is_told_there_is_more()
    {
        var executor = await BuildAsync(maxPageSize: 3, defaultPageSize: 2);

        var json = (await executor.ExecuteAsync(
            "{ shelves { nodes { name books { nodes { title } pageInfo { hasNextPage } } } } }")).ToJson();

        var books = ShelfNode(json, "fiction").GetProperty("books");
        Assert.Equal(2, books.GetProperty("nodes").GetArrayLength());
        Assert.True(books.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(0, ShelfNode(json, "empty").GetProperty("books").GetProperty("nodes").GetArrayLength());
    }

    [Fact]
    public async Task A_collection_page_larger_than_the_maximum_is_refused()
    {
        var executor = await BuildAsync(maxPageSize: 3, defaultPageSize: 2);

        var json = (await executor.ExecuteAsync("{ shelves { nodes { name books(first: 4) { nodes { title } } } } }")).ToJson();

        Assert.Contains("\"errors\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("book-", json, StringComparison.Ordinal);
    }

    /// <summary>The page is a slice of the key order — the same rule as a query field — so a client sees stable pages.</summary>
    [Fact]
    public async Task A_collection_page_is_a_slice_of_the_key_order()
    {
        var executor = await BuildAsync(maxPageSize: 10, defaultPageSize: 10);

        var json = (await executor.ExecuteAsync("{ shelves { nodes { name books { nodes { id title } } } } }")).ToJson();

        var ids = ShelfNode(json, "fiction").GetProperty("books").GetProperty("nodes").EnumerateArray()
            .Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(BookCount, ids.Count);
        Assert.Equal(ids.OrderBy(id => id.ToString(), StringComparer.OrdinalIgnoreCase).ToList(), ids);
    }
}
