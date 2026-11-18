using System.Security.Claims;
using System.Text.Json;
using HotChocolate;
using HotChocolate.Execution;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.GraphQL;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

/// <summary>
/// A connection says how many rows the caller may read, as OData's <c>$count</c> does: the page is a slice, the total is the
/// set. The count is a selected field — a request that does not ask for it does not run the counting query — and it is behind
/// the same read policy as the rows it counts.
/// </summary>
public sealed class GraphQLTotalCountTests : IDisposable
{
    public sealed class Ledger : IyuEntity
    {
        public string Name { get; set; } = "";
        public List<Entry> Entries { get; set; } = [];
    }

    public sealed class Entry : IyuEntity
    {
        public string Text { get; set; } = "";
        public Guid LedgerId { get; set; }
        public Ledger? Ledger { get; set; }
    }

    public sealed class Vault : IyuEntity
    {
        public string Code { get; set; } = "";
    }

    public sealed class TotalContext(DbContextOptions<TotalContext> options) : IyuDbContext(options)
    {
        public DbSet<Ledger> Ledgers => Set<Ledger>();
        public DbSet<Entry> Entries => Set<Entry>();
        public DbSet<Vault> Vaults => Set<Vault>();
    }

    private const string VaultPolicy = "vault.read";
    private readonly SharedMemorySqlite _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class CountingReads : DbCommandInterceptor
    {
        public int Counts;

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Counts);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        // EF runs a Count as a scalar command — async or sync, whichever the paging code calls.
        public override InterceptionResult<object> ScalarExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            if (command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Counts);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            if (command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Counts);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Counts);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private readonly CountingReads _counting = new();

    private async Task<ServiceProvider> BuildAsync(int maxPageSize = 3, int defaultPageSize = 2)
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = maxPageSize, DefaultPageSize = defaultPageSize };
        graphql.AddEntityPair<Ledger, Ledger>("ledgers", "ledger");
        graphql.AddEntityPair<Entry, Entry>("entries", "entry");
        graphql.AddEntityPair<Vault, Vault>("vaults", "vault").Restrict("vaults", VaultPolicy);

        var services = new ServiceCollection();
        services.AddDbContext<TotalContext>(o => o.UseSqlite(_db.ConnectionString).AddInterceptors(_counting));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<TotalContext>());
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization(o => o.AddPolicy(VaultPolicy, p => p.RequireClaim("perm", VaultPolicy)));
        graphql.ApplyTo(services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TotalContext>();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Ledgers.Add(new Ledger { Name = "five", Entries = Enumerable.Range(0, 5).Select(i => new Entry { Text = $"e{i}" }).ToList() });
        ctx.Ledgers.Add(new Ledger { Name = "two", Entries = [new Entry { Text = "a" }, new Entry { Text = "b" }] });
        ctx.Ledgers.Add(new Ledger { Name = "none" });
        ctx.Vaults.AddRange(new Vault { Code = "x" }, new Vault { Code = "y" });
        await ctx.SaveChangesAsync();
        return sp;
    }

    private static void SetCurrentUser(IServiceProvider services, params string[] permissions)
    {
        var identity = permissions.Length == 0
            ? new ClaimsIdentity()
            : new ClaimsIdentity(permissions.Select(p => new Claim("perm", p)), authenticationType: "Test");
        services.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static JsonElement Root(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        return root;
    }

    [Fact]
    public async Task A_page_is_a_slice_and_the_total_is_the_set()
    {
        await using var sp = await BuildAsync();
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var json = (await executor.ExecuteAsync("{ entries { totalCount nodes { text } pageInfo { hasNextPage } } }")).ToJson();

        var entries = Root(json).GetProperty("data").GetProperty("entries");
        Assert.Equal(7, entries.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, entries.GetProperty("nodes").GetArrayLength());
        Assert.True(entries.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
    }

    /// <summary>A page limit bounds the page, never the answer: the total counts past the maximum page size.</summary>
    [Fact]
    public async Task The_total_is_not_bounded_by_the_page_size()
    {
        await using var sp = await BuildAsync(maxPageSize: 1, defaultPageSize: 1);
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var json = (await executor.ExecuteAsync("{ entries(first: 1) { totalCount nodes { text } } }")).ToJson();

        Assert.Equal(7, Root(json).GetProperty("data").GetProperty("entries").GetProperty("totalCount").GetInt32());
    }

    /// <summary>A collection's total is that parent's children — one answer per parent, not the table's.</summary>
    [Fact]
    public async Task A_collections_total_counts_one_parents_children()
    {
        await using var sp = await BuildAsync();
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var json = (await executor.ExecuteAsync(
            "{ ledgers(first: 3) { nodes { name entries(first: 1) { totalCount nodes { text } } } } }")).ToJson();

        var totals = Root(json).GetProperty("data").GetProperty("ledgers").GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => n.GetProperty("name").GetString()!, n => n.GetProperty("entries").GetProperty("totalCount").GetInt32());
        Assert.Equal(5, totals["five"]);
        Assert.Equal(2, totals["two"]);
        Assert.Equal(0, totals["none"]);
    }

    [Fact]
    public async Task A_request_that_does_not_ask_for_the_total_does_not_run_the_counting_query()
    {
        await using var sp = await BuildAsync();
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        _counting.Counts = 0;
        await executor.ExecuteAsync("{ entries { nodes { text } } }");
        var withoutTotal = _counting.Counts;

        _counting.Counts = 0;
        await executor.ExecuteAsync("{ entries { totalCount nodes { text } } }");
        var withTotal = _counting.Counts;

        Assert.Equal(0, withoutTotal);
        Assert.True(withTotal >= 1, "asking for the total runs a counting query");
    }

    /// <summary>The count is behind the same policy as the rows: a caller refused the set is not told its size.</summary>
    [Fact]
    public async Task A_caller_refused_a_set_is_not_told_how_many_rows_it_holds()
    {
        await using var sp = await BuildAsync();
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        SetCurrentUser(sp);
        _counting.Counts = 0;
        var refused = (await executor.ExecuteAsync("{ vaults { totalCount } }")).ToJson();

        Assert.Contains("\"errors\"", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("\"totalCount\":2", refused, StringComparison.Ordinal);
        Assert.Equal(0, _counting.Counts);

        // The contrast that makes the refusal mean something: the same request from a caller holding the policy.
        SetCurrentUser(sp, VaultPolicy);
        var allowed = (await executor.ExecuteAsync("{ vaults { totalCount } }")).ToJson();
        Assert.Equal(2, Root(allowed).GetProperty("data").GetProperty("vaults").GetProperty("totalCount").GetInt32());
    }
}
