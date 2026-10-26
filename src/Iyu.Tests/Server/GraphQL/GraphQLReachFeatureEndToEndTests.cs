using System.Net;
using System.Net.Http.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using HotChocolate;
using HotChocolate.Execution;
using Iyu.Server.GraphQL;
using Iyu.Tests.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

/// <summary>
/// A host that records what each request read has to find, for a GraphQL request, the entities it selects — through the
/// real HTTP pipeline, since the reach is put on <c>HttpContext.Features</c> before the request runs. The same answer the
/// OData surface hands on (<c>IIyuODataReachFeature</c>), without the host parsing the document itself.
/// </summary>
public sealed class GraphQLReachFeatureEndToEndTests : IAsyncLifetime
{
    public sealed class ReachShelf : IyuEntity
    {
        public string Name { get; set; } = "";
        public List<ReachBook> Books { get; set; } = [];
    }

    public sealed class ReachBook : IyuEntity
    {
        public string Title { get; set; } = "";
        public Guid ShelfId { get; set; }
        public ReachShelf? Shelf { get; set; }
        public List<ReachReview> Reviews { get; set; } = [];
    }

    public sealed class ReachReview : IyuEntity
    {
        public string Text { get; set; } = "";
        public Guid BookId { get; set; }
        public ReachBook? Book { get; set; }
    }

    public sealed class ReachContext(DbContextOptions<ReachContext> options) : IyuDbContext(options)
    {
        public DbSet<ReachShelf> Shelves => Set<ReachShelf>();
        public DbSet<ReachBook> Books => Set<ReachBook>();
        public DbSet<ReachReview> Reviews => Set<ReachReview>();
    }

    private static readonly List<string> Log = [];

    private sealed class Capture(List<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) lock (sink) sink.Add(exception.ToString()); }
    }

    private const string BooksPolicy = "books.read";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<WebApplication> _apps = [];

    public Task InitializeAsync()
    {
        _connection.Open();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps) await app.DisposeAsync();
        _connection.Dispose();
    }

    /// <summary>What the host's recording middleware saw once the request had run.</summary>
    private sealed class Seen
    {
        public IIyuGraphQLReachFeature? Reach;
    }

    private async Task<(HttpClient Client, Seen Seen)> Host(bool computeReach, bool restrictBooks = false)
    {
        var graphql = new IyuGraphQLSchemaBuilder { ComputeReach = computeReach, MaxPageSize = 3, DefaultPageSize = 2 };
        graphql.AddEntityPair<ReachShelf, ReachShelf>("shelves", "shelf");
        graphql.AddEntityPair<ReachBook, ReachBook>("books", "book");
        graphql.AddEntityPair<ReachReview, ReachReview>("reviews", "review");
        if (restrictBooks) graphql.Restrict("books", BooksPolicy);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Capture(Log));
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<ReachContext>(o => o.UseSqlite(_connection));
        builder.Services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<ReachContext>());
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorizationBuilder().AddPolicy(BooksPolicy, p => p.RequireClaim("perm", BooksPolicy));
        graphql.ApplyTo(builder.Services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));

        var app = builder.Build();
        var seen = new Seen();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            await next();
            seen.Reach = context.Features.Get<IIyuGraphQLReachFeature>();
        });
        app.MapGraphQL();
        await app.StartAsync();
        _apps.Add(app);

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ReachContext>().Database.EnsureCreatedAsync();

        return (app.GetTestServer().CreateClient(), seen);
    }

    private static async Task<HttpResponseMessage> Post(
        HttpClient client, string query, string? operationName = null, string? perm = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new { query, operationName }),
        };
        if (perm is not null) request.Headers.Add("X-Test-Perm", perm);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_root_query_alone_is_addressed_and_reaches_nothing_else()
    {
        var (client, seen) = await Host(computeReach: true);

        using var response = await Post(client, "{ shelves { nodes { name } } }");

        Assert.True(response.StatusCode == HttpStatusCode.OK, string.Join(" --- ", Log));
        Assert.Equal(["shelves"], seen.Reach!.AddressedQueries);
        Assert.Empty(seen.Reach.ReachedQueries);
    }

    [Fact]
    public async Task A_navigation_reaches_the_entity_it_leads_to()
    {
        var (client, seen) = await Host(computeReach: true);

        using var response = await Post(client, "{ shelves { nodes { name books { nodes { title } } } } }");

        Assert.Equal(["shelves"], seen.Reach!.AddressedQueries);
        Assert.Equal(["books"], seen.Reach.ReachedQueries);
        _ = response;
    }

    [Fact]
    public async Task Edges_nodes_aliases_and_a_single_valued_navigation_are_followed()
    {
        var (client, seen) = await Host(computeReach: true);

        using var response = await Post(client,
            "{ b: books { edges { node { title reviews { nodes { text } } owner: shelf { name } } } } }");

        Assert.Equal(["books"], seen.Reach!.AddressedQueries);
        Assert.Equal(["reviews", "shelves"], seen.Reach.ReachedQueries);
        _ = response;
    }

    [Fact]
    public async Task Named_and_inline_fragments_are_expanded()
    {
        var (client, seen) = await Host(computeReach: true);

        using var response = await Post(client, """
            query { shelves { nodes { ...WithBooks ... on ReachShelf { name } } } }
            fragment WithBooks on ReachShelf { books { nodes { ... on ReachBook { reviews { nodes { text } } } } } }
            """);

        Assert.Equal(["shelves"], seen.Reach!.AddressedQueries);
        Assert.Equal(["books", "reviews"], seen.Reach.ReachedQueries);
        _ = response;
    }

    [Fact]
    public async Task A_navigation_that_leads_back_to_an_addressed_entity_names_it_as_reached_too()
    {
        var (client, seen) = await Host(computeReach: true);

        using var response = await Post(client, "{ shelves { nodes { books { nodes { shelf { name } } } } } }");

        Assert.Equal(["shelves"], seen.Reach!.AddressedQueries);
        Assert.Equal(["books", "shelves"], seen.Reach.ReachedQueries);
        _ = response;
    }

    [Fact]
    public async Task Only_the_operation_the_request_names_counts()
    {
        var (client, seen) = await Host(computeReach: true);

        using var response = await Post(client,
            "query A { shelves { nodes { name } } } query B { books { nodes { title } } }", operationName: "B");

        Assert.Equal(["books"], seen.Reach!.AddressedQueries);
        _ = response;
    }

    [Fact]
    public async Task A_request_the_read_policy_refuses_still_carries_what_it_tried_to_reach()
    {
        var (client, seen) = await Host(computeReach: false, restrictBooks: true);

        using var response = await Post(client, "{ shelves { nodes { name } } books { nodes { title } } }");

        Assert.Contains("\"errors\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(["books", "shelves"], seen.Reach!.AddressedQueries);   // the policy refused books; the record still says it was tried
        Assert.Empty(seen.Reach.ReachedQueries);
    }

    [Fact]
    public async Task Without_the_switch_and_without_a_policy_nothing_is_computed()
    {
        var (client, seen) = await Host(computeReach: false);

        using var response = await Post(client, "{ shelves { nodes { name } } }");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(seen.Reach);
    }

    [Fact]
    public async Task A_document_that_does_not_parse_has_no_reach_and_the_executor_reports_the_error()
    {
        var (client, seen) = await Host(computeReach: true);

        using var response = await Post(client, "{ shelves { nodes { ");

        Assert.Contains("\"errors\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(seen.Reach);
    }

    /// <summary>
    /// The document is read before it is validated, so a request built to blow up the reading must not: fragments that
    /// each spread the next one twice are 2^n paths but n fragments. Read in isolation — the executor's own handling of
    /// such a document is a separate matter (see the issue draft on the GraphQL surface).
    /// </summary>
    [Fact]
    public async Task Fragments_that_spread_each_other_many_times_over_are_read_once_each()
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = 3, DefaultPageSize = 2 };
        graphql.AddEntityPair<ReachShelf, ReachShelf>("shelves", "shelf");
        graphql.AddEntityPair<ReachBook, ReachBook>("books", "book");
        graphql.AddEntityPair<ReachReview, ReachReview>("reviews", "review");
        var services = new ServiceCollection();
        services.AddDbContext<ReachContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<ReachContext>());
        services.AddLogging();
        graphql.ApplyTo(services.AddGraphQLServer());
        var executor = await services.BuildServiceProvider().GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        const int levels = 40;
        var text = new System.Text.StringBuilder("query { shelves { nodes { ...F0 } } } ");
        for (var i = 0; i < levels; i++)
            text.Append($"fragment F{i} on ReachShelf {{ ...F{i + 1} ...F{i + 1} }} ");
        text.Append($"fragment F{levels} on ReachShelf {{ books {{ nodes {{ title }} }} }} ");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var reach = IyuGraphQLReach.Of(
            executor.Schema, HotChocolate.Language.Utf8GraphQLParser.Parse(text.ToString()), null, graphql.QueryNames, graphql.QueryNameOf);
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"took {started.Elapsed}");
        Assert.Equal(["shelves"], reach!.Value.Addressed);
        Assert.Equal(["books"], reach.Value.Reached);
    }

    [Fact]
    public async Task A_document_past_the_reading_budget_records_no_reach()
    {
        var (client, seen) = await Host(computeReach: true);
        var fields = string.Join(" ", Enumerable.Range(0, IyuGraphQLReachBudget + 1).Select(i => $"a{i}: name"));

        using var response = await Post(client, $"{{ shelves {{ nodes {{ {fields} }} }} }}");

        Assert.Null(seen.Reach);
        _ = response;
    }

    // Mirrors IyuGraphQLReach.MaxSelections (internal): one more selection than the reading allows.
    private const int IyuGraphQLReachBudget = 10_000;

    [Fact]
    public async Task The_request_still_runs_normally_with_the_reach_computed()
    {
        var (client, _) = await Host(computeReach: true);
        using (var scope = _apps[^1].Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReachContext>();
            db.Shelves.Add(new ReachShelf { Name = "fiction", Books = [new ReachBook { Title = "b1" }] });
            await db.SaveChangesAsync();
        }

        using var response = await Post(client, "{ shelves { nodes { name books { nodes { title } } } } }");

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("fiction", json, StringComparison.Ordinal);
        Assert.Contains("b1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"errors\"", json, StringComparison.Ordinal);
    }
}
