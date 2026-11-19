using System.Data.Common;
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
/// A condition or an ordering that crosses a single reference (<c>where: { customer: { name: ... } }</c>) reads the model it
/// crosses into: that model's own read policy and the policy of every column named there apply, before any query runs, and the
/// request is recorded as having reached it.
/// </summary>
public sealed class GraphQLWhereOrderCrossingTests : IDisposable
{
    public sealed class Customer : IyuEntity
    {
        public string Name { get; set; } = "";
        public int Credit { get; set; }
        public string Notes { get; set; } = "";
        public List<Purchase> Purchases { get; set; } = [];
    }

    public sealed class Purchase : IyuEntity
    {
        public string Item { get; set; } = "";
        public int Amount { get; set; }
        public Guid CustomerId { get; set; }
        public Customer? Customer { get; set; }
        public Guid? ReplacesId { get; set; }
        public Purchase? Replaces { get; set; }
        public Guid? WarehouseId { get; set; }
        public Warehouse? Warehouse { get; set; }
    }

    /// <summary>A model no query field serves: a reference to it is not offered to a condition.</summary>
    public sealed class Warehouse : IyuEntity
    {
        public string Code { get; set; } = "";
    }

    public sealed class CrossContext(DbContextOptions<CrossContext> options) : IyuDbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Purchase> Purchases => Set<Purchase>();
        public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    }

    private const string CustomersPolicy = "customers.read";
    private const string CreditPolicy = "credit.read";
    private readonly SharedMemorySqlite _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class CountingCommands : DbCommandInterceptor
    {
        public int Commands;

        private void Note(DbCommand command)
        {
            if (command.CommandText.Contains("Purchases", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("Customers", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Commands);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Note(command); return base.ReaderExecuting(command, eventData, result); }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Note(command); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Note(command); return base.ScalarExecuting(command, eventData, result); }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Note(command); return base.ScalarExecutingAsync(command, eventData, result, cancellationToken); }
    }

    private readonly CountingCommands _counting = new();

    private async Task<ServiceProvider> BuildAsync(Action<IyuGraphQLSchemaBuilder>? configure = null)
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = 5, DefaultPageSize = 5, WhereAndOrder = true };
        graphql.AddEntityPair<Customer, Customer>("customers", "customer");
        graphql.AddEntityPair<Purchase, Purchase>("purchases", "purchase");
        graphql.Restrict("customers", CustomersPolicy);
        graphql.RestrictProperty(typeof(Customer), nameof(Customer.Credit), CreditPolicy);
        graphql.Exclude<Customer>(c => c.Notes);
        configure?.Invoke(graphql);

        var services = new ServiceCollection();
        services.AddDbContext<CrossContext>(o => o.UseSqlite(_db.ConnectionString).AddInterceptors(_counting));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<CrossContext>());
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization(o =>
        {
            o.AddPolicy(CustomersPolicy, p => p.RequireClaim("perm", CustomersPolicy));
            o.AddPolicy(CreditPolicy, p => p.RequireClaim("perm", CreditPolicy));
        });
        graphql.ApplyTo(services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CrossContext>();
        await ctx.Database.EnsureCreatedAsync();
        var ann = new Customer { Name = "ann", Credit = 100, Notes = "n1" };
        var bob = new Customer { Name = "bob", Credit = 5, Notes = "n2" };
        var first = new Purchase { Item = "pen", Amount = 1, Customer = ann };
        ctx.Customers.AddRange(ann, bob);
        ctx.Purchases.AddRange(
            first,
            new Purchase { Item = "ink", Amount = 2, Customer = ann, Replaces = first },
            new Purchase { Item = "pad", Amount = 3, Customer = bob },
            new Purchase { Item = "cap", Amount = 4, Customer = bob });
        await ctx.SaveChangesAsync();
        Volatile.Write(ref _counting.Commands, 0);
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

    private static async Task<string> RunAsync(IServiceProvider sp, string query, Dictionary<string, object?>? variables = null)
    {
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var request = OperationRequestBuilder.New().SetDocument(query);
        if (variables is not null) request.SetVariableValues(variables);
        return (await executor.ExecuteAsync(request.Build())).ToJson();
    }

    private static JsonElement Purchases(string json) => JsonDocument.Parse(json).RootElement.GetProperty("data").GetProperty("purchases");

    private static string[] Items(string json)
        => Purchases(json).GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("item").GetString()!).ToArray();

    private static string Block(string sdl, string header)
    {
        var start = sdl.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{header}' in the schema");
        return sdl[start..sdl.IndexOf("\n}", start, StringComparison.Ordinal)];
    }

    // ---- the schema ----

    [Fact]
    public async Task A_condition_may_cross_a_reference_to_a_served_model_and_nothing_else()
    {
        await using var sp = await BuildAsync();
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var sdl = executor.Schema.ToString();

        var purchaseFilter = Block(sdl, "input PurchaseFilterInput");
        var purchaseSort = Block(sdl, "input PurchaseSortInput");
        var customerFilter = Block(sdl, "input CustomerFilterInput");

        Assert.Contains("customer: CustomerFilterInput", purchaseFilter);
        Assert.Contains("replaces: PurchaseFilterInput", purchaseFilter);   // a reference to its own model is crossable too
        Assert.Contains("customer: CustomerSortInput", purchaseSort);
        // A model no field serves is not crossable; its key column is shown by the output type, so it is a column like another.
        Assert.DoesNotContain("warehouse:", purchaseFilter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warehouse:", purchaseSort, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warehouseId:", purchaseFilter);
        Assert.DoesNotContain("purchases", customerFilter, StringComparison.OrdinalIgnoreCase);   // a collection is not crossed here
        Assert.DoesNotContain("notes", customerFilter, StringComparison.OrdinalIgnoreCase);       // the target's own exposure rules apply
        Assert.Contains("credit:", customerFilter);
    }

    // ---- what it does for a caller who may read everything ----

    [Fact]
    public async Task A_condition_on_the_referenced_row_narrows_the_rows_and_the_total()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, CustomersPolicy, CreditPolicy);

        var json = await RunAsync(sp, "{ purchases(where: { customer: { name: { eq: \"bob\" } } }) { totalCount nodes { item } } }");

        Assert.Equal(2, Purchases(json).GetProperty("totalCount").GetInt32());
        Assert.Equal(["cap", "pad"], Items(json).Order().ToArray());
    }

    [Fact]
    public async Task An_ordering_by_the_referenced_rows_column_keeps_the_key_as_its_last_criterion()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, CustomersPolicy, CreditPolicy);

        var all = await RunAsync(sp, "{ purchases(order: { customer: { name: DESC } }) { nodes { item customer { name } } } }");

        var owners = Purchases(all).GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("customer").GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["bob", "bob", "ann", "ann"], owners);
    }

    [Fact]
    public async Task Paging_through_an_ordering_by_the_referenced_rows_column_sees_every_row_exactly_once()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, CustomersPolicy, CreditPolicy);
        var seen = new List<string>();
        string? after = null;

        for (var page = 0; page < 10; page++)
        {
            var json = await RunAsync(sp,
                "query($after: String) { purchases(first: 1, after: $after, order: { customer: { name: ASC } }) { nodes { item } pageInfo { endCursor hasNextPage } } }",
                new() { ["after"] = after });
            seen.AddRange(Items(json));
            var info = Purchases(json).GetProperty("pageInfo");
            if (!info.GetProperty("hasNextPage").GetBoolean()) break;
            after = info.GetProperty("endCursor").GetString();
        }

        Assert.Equal(["cap", "ink", "pad", "pen"], seen.Order().ToArray());
        Assert.Equal(4, seen.Count);   // ties on the referenced column (two rows per customer) neither repeat nor skip a row
    }

    [Fact]
    public async Task A_condition_crosses_two_references_and_a_reference_to_its_own_model()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, CustomersPolicy, CreditPolicy);

        var json = await RunAsync(sp,
            "{ purchases(where: { replaces: { customer: { credit: { gt: 50 } } } }) { nodes { item } } }");

        Assert.Equal(["ink"], Items(json));
    }

    // ---- the read rule across a reference ----

    public static TheoryData<string, string?> WaysToCrossIntoCustomers => new()
    {
        { "{ purchases(where: { customer: { name: { eq: \"x\" } } }) { nodes { item } } }", null },
        { "{ purchases(order: { customer: { name: ASC } }) { nodes { item } } }", null },
        { "{ purchases(where: { and: [{ amount: { gt: 0 } }, { or: [{ customer: { name: { eq: \"x\" } } }] }] }) { totalCount } }", null },
        { "{ purchases(where: { replaces: { customer: { name: { eq: \"x\" } } } }) { totalCount } }", null },
        { "{ purchases(where: { customer: {} }) { totalCount } }", null },
        { "query($w: PurchaseFilterInput) { purchases(where: $w) { totalCount } }", "w" },
        { "query($w: PurchaseFilterInput = { customer: { name: { eq: \"x\" } } }) { purchases(where: $w) { totalCount } }", null },
        { "{ ...F } fragment F on Query { purchases(order: { customer: { name: ASC } }) { totalCount } }", null },
    };

    private static Dictionary<string, object?>? VariablesFor(string? name) => name switch
    {
        "w" => new() { ["w"] = new Dictionary<string, object?> { ["customer"] = new Dictionary<string, object?> { ["name"] = new Dictionary<string, object?> { ["eq"] = "x" } } } },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(WaysToCrossIntoCustomers))]
    public async Task A_caller_who_may_not_read_the_referenced_model_cannot_cross_into_it_and_no_query_runs(string query, string? variable)
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp);   // signed in with no permission at all

        var json = await RunAsync(sp, query, VariablesFor(variable));

        Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    [Theory]
    [MemberData(nameof(WaysToCrossIntoCustomers))]
    public async Task The_same_request_from_a_caller_who_may_read_it_is_answered(string query, string? variable)
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, CustomersPolicy);

        var json = await RunAsync(sp, query, VariablesFor(variable));

        Assert.DoesNotContain("\"errors\"", json, StringComparison.Ordinal);
        Assert.True(Volatile.Read(ref _counting.Commands) > 0);
    }

    [Fact]
    public async Task Crossing_with_null_is_crossing_all_the_same_and_is_refused_without_a_query_for_a_caller_who_may_not()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp);

        var json = await RunAsync(sp, "{ purchases(where: { customer: null }) { totalCount } }");

        Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    [Theory]
    [InlineData("{ purchases(where: { customer: { credit: { gt: 50 } } }) { totalCount } }")]
    [InlineData("{ purchases(order: { customer: { credit: DESC } }) { totalCount } }")]
    [InlineData("{ purchases(where: { replaces: { customer: { credit: { lt: 1 } } } }) { totalCount } }")]
    public async Task A_column_with_its_own_policy_in_the_referenced_model_needs_that_policy_too(string query)
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, CustomersPolicy);   // may read customers, not their credit

        var json = await RunAsync(sp, query);

        Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    [Fact]
    public async Task A_column_without_a_policy_in_a_readable_model_is_open()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, CustomersPolicy);

        var json = await RunAsync(sp, "{ purchases(where: { customer: { name: { eq: \"ann\" } } }) { totalCount } }");

        Assert.Equal(2, Purchases(json).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task The_models_own_columns_do_not_need_the_referenced_models_policy()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp);

        var json = await RunAsync(sp, "{ purchases(where: { amount: { gt: 2 } }, order: { item: ASC }) { nodes { item } } }");

        Assert.Equal(["cap", "pad"], Items(json));
    }

    // ---- how far it may go ----

    [Fact]
    public async Task A_chain_of_references_longer_than_the_bound_is_refused_and_no_query_runs()
    {
        await using var sp = await BuildAsync(configure: g => g.MaxConditionDepth = 2);
        SetCurrentUser(sp, CustomersPolicy, CreditPolicy);

        var json = await RunAsync(sp,
            "{ purchases(where: { replaces: { replaces: { replaces: { amount: { gt: 0 } } } } }) { totalCount } }");

        Assert.Contains("\"IYU_CONDITION_REFUSED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    // ---- the request's reach ----

    [Fact]
    public async Task A_condition_that_crosses_into_a_model_puts_it_in_the_reach_even_when_it_is_refused()
    {
        await using var sp = await BuildAsync(configure: g => g.ComputeReach = true);
        SetCurrentUser(sp);
        var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;

        var json = await RunAsync(sp, "{ purchases(where: { customer: { name: { eq: \"x\" } } }) { totalCount } }");

        Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", json, StringComparison.Ordinal);
        var reach = http.Features.Get<IIyuGraphQLReachFeature>();
        Assert.NotNull(reach);
        Assert.Contains("customers", reach!.ReachedQueries);
        Assert.Equal(["purchases"], reach.AddressedQueries);
    }
}
