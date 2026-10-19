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
        public List<Review> Reviews { get; set; } = [];
    }

    public sealed class Review : IyuEntity
    {
        public string Text { get; set; } = "";
        public Guid BookId { get; set; }
        public Book? Book { get; set; }
    }

    public sealed class ShelfContext(DbContextOptions<ShelfContext> options) : IyuDbContext(options)
    {
        public DbSet<Shelf> Shelves => Set<Shelf>();
        public DbSet<Book> Books => Set<Book>();
        public DbSet<Review> Reviews => Set<Review>();
    }

    private const int BookCount = 5;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public GraphQLNestedCollectionPagingTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    /// <summary>Counts the commands that read the books table — one per batch, not one per shelf, is the point.</summary>
    private sealed class BookReads : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Count;
        public int ReviewCount;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Books\"", StringComparison.Ordinal)) Interlocked.Increment(ref Count);
            if (command.CommandText.Contains("FROM \"Reviews\"", StringComparison.Ordinal)) Interlocked.Increment(ref ReviewCount);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private readonly BookReads _bookReads = new();

    private async Task<IRequestExecutor> BuildAsync(int maxPageSize, int defaultPageSize, int extraShelves = 0)
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = maxPageSize, DefaultPageSize = defaultPageSize };
        graphql.AddEntityPair<Shelf, Shelf>("shelves", "shelf");
        graphql.AddEntityPair<Book, Book>("books", "book");
        graphql.AddEntityPair<Review, Review>("reviews", "review");

        var services = new ServiceCollection();
        services.AddDbContext<ShelfContext>(o => o.UseSqlite(_connection).AddInterceptors(_bookReads));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<ShelfContext>());
        services.AddLogging();
        graphql.ApplyTo(services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ShelfContext>();
            await ctx.Database.EnsureCreatedAsync();
            var shelf = new Shelf { Name = "fiction" };
            for (var i = 0; i < BookCount; i++)
            {
                var book = new Book { Title = $"book-{i}" };
                // Every book but the last has two reviews: a page of books with and without reviews.
                if (i < BookCount - 1) book.Reviews.AddRange([new Review { Text = $"r{i}-a" }, new Review { Text = $"r{i}-b" }]);
                shelf.Books.Add(book);
            }
            ctx.Shelves.Add(shelf);
            ctx.Shelves.Add(new Shelf { Name = "empty" });
            for (var s = 0; s < extraShelves; s++)
                ctx.Shelves.Add(new Shelf { Name = $"extra-{s}", Books = [new Book { Title = $"extra-{s}-a" }, new Book { Title = $"extra-{s}-b" }] });
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

    /// <summary>
    /// A page of parents reads its children's pages in one query — not one per parent — and each parent still gets only
    /// its own children, a page of them.
    /// </summary>
    [Fact]
    public async Task A_page_of_parents_reads_their_collections_in_one_query()
    {
        var executor = await BuildAsync(maxPageSize: 10, defaultPageSize: 10, extraShelves: 4);
        _bookReads.Count = 0;

        var json = (await executor.ExecuteAsync("{ shelves { nodes { name books(first: 3) { nodes { title } } } } }")).ToJson();

        Assert.Equal(1, _bookReads.Count);
        Assert.Equal(3, ShelfNode(json, "fiction").GetProperty("books").GetProperty("nodes").GetArrayLength());
        Assert.Equal(["extra-2-a", "extra-2-b"], ShelfNode(json, "extra-2").GetProperty("books").GetProperty("nodes")
            .EnumerateArray().Select(b => b.GetProperty("title").GetString()).Order());
        Assert.Equal(0, ShelfNode(json, "empty").GetProperty("books").GetProperty("nodes").GetArrayLength());
    }

    /// <summary>
    /// A collection inside a collection: each level is one batch — the books of every shelf in one query, the reviews of
    /// every book on those pages in one more — and each book gets its own reviews, which needs the book's key to survive
    /// the books' projection when the selection does not name it.
    /// </summary>
    [Fact]
    public async Task A_collection_inside_a_collection_is_one_batch_per_level()
    {
        var executor = await BuildAsync(maxPageSize: 10, defaultPageSize: 10, extraShelves: 2);
        _bookReads.Count = 0;
        _bookReads.ReviewCount = 0;

        var json = (await executor.ExecuteAsync(
            // Each level names its page size: the cost analysis multiplies the levels' sizes and refuses the defaults.
            "{ shelves(first: 5) { nodes { name books(first: 5) { nodes { title reviews(first: 5) { nodes { text } } } } } } }")).ToJson();

        Assert.Equal(1, _bookReads.Count);
        // Batched, not one query per book (the page holds 9 books). The scheduler dispatches a batch when the request has
        // no other work ready, and the books' continuations can reach it apart under load — so a batch may split (seen: in
        // two, with the whole suite running); what holds is fewer queries than books.
        Assert.InRange(_bookReads.ReviewCount, 1, 8);
        var books = ShelfNode(json, "fiction").GetProperty("books").GetProperty("nodes").EnumerateArray()
            .ToDictionary(b => b.GetProperty("title").GetString()!, b => b.GetProperty("reviews").GetProperty("nodes")
                .EnumerateArray().Select(r => r.GetProperty("text").GetString()).Order().ToList());
        Assert.Equal(["r0-a", "r0-b"], books["book-0"]);
        Assert.Equal(["r3-a", "r3-b"], books["book-3"]);
        Assert.Empty(books[$"book-{BookCount - 1}"]);
    }

    /// <summary>The next page continues where the first ended — the cursor of a batched page reads back.</summary>
    [Fact]
    public async Task The_cursor_of_a_collection_page_reads_the_next_page()
    {
        var executor = await BuildAsync(maxPageSize: 10, defaultPageSize: 10);

        var first = ShelfNode((await executor.ExecuteAsync(
            "{ shelves { nodes { name books(first: 2) { nodes { id } pageInfo { endCursor hasNextPage } } } } }")).ToJson(), "fiction")
            .GetProperty("books");
        var cursor = first.GetProperty("pageInfo").GetProperty("endCursor").GetString();
        var second = ShelfNode((await executor.ExecuteAsync(
            $$"""{ shelves { nodes { name books(first: 10, after: "{{cursor}}") { nodes { id } } } } }""")).ToJson(), "fiction")
            .GetProperty("books");

        var seen = first.GetProperty("nodes").EnumerateArray().Concat(second.GetProperty("nodes").EnumerateArray())
            .Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(BookCount, seen.Count);
        Assert.Equal(BookCount, seen.Distinct().Count());
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
