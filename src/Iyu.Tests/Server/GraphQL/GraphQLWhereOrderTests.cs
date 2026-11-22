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
/// <c>where</c> and <c>order</c> on a query field (opt-in): the input types name only the columns the output type shows, a
/// condition narrows the rows and the total, an ordering is stable across pages, and a caller who may not read a column
/// cannot filter or sort by it — by any way of writing the request — with no query run.
/// </summary>
public sealed class GraphQLWhereOrderTests : IDisposable
{
    public sealed class Account : IyuEntity
    {
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public string Secret { get; set; } = "";
        public int Salary { get; set; }

        /// <summary>Left out of the output type by attribute, so out of the input types too.</summary>
        [GraphQLIgnore]
        public string Memo { get; set; } = "";
    }

    public sealed class ColumnShapes
    {
        public string Plain { get; set; } = "";
        public int? Optional { get; set; }
        public DateTimeOffset When { get; set; }
        public Kind Which { get; set; }
        [GraphQLIgnore] public string Ignored { get; set; } = "";
        public string NotPublicToRead { internal get; set; } = "";
        public string WriteOnly { set { } }
        public List<string> Tags { get; set; } = [];
        public Account? Owner { get; set; }
        public int this[int index] => index;
    }

    public enum Kind { A, B }

    public sealed class WhereContext(DbContextOptions<WhereContext> options) : IyuDbContext(options)
    {
        public DbSet<Account> Accounts => Set<Account>();
    }

    private const string SalaryPolicy = "salary.read";
    private readonly SharedMemorySqlite _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Every command the database runs against the accounts — rows, counts, either path.</summary>
    private sealed class CountingCommands : DbCommandInterceptor
    {
        public int Commands;

        private void Note(DbCommand command)
        {
            if (command.CommandText.Contains("Accounts", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Commands);
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

    private async Task<ServiceProvider> BuildAsync(bool whereAndOrder = true, Action<IyuGraphQLSchemaBuilder>? configure = null)
    {
        var graphql = new IyuGraphQLSchemaBuilder { MaxPageSize = 3, DefaultPageSize = 2, WhereAndOrder = whereAndOrder };
        graphql.AddEntityPair<Account, Account>("accounts", "account");
        graphql.Exclude<Account>(a => a.Secret);
        graphql.RestrictProperty(typeof(Account), nameof(Account.Salary), SalaryPolicy);
        configure?.Invoke(graphql);

        var services = new ServiceCollection();
        services.AddDbContext<WhereContext>(o => o.UseSqlite(_db.ConnectionString).AddInterceptors(_counting));
        services.AddScoped<IyuDbContext>(sp => sp.GetRequiredService<WhereContext>());
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization(o => o.AddPolicy(SalaryPolicy, p => p.RequireClaim("perm", SalaryPolicy)));
        graphql.ApplyTo(services.AddGraphQLServer().ModifyRequestOptions(o => o.IncludeExceptionDetails = true));
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<WhereContext>();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Accounts.AddRange(
            new Account { Name = "ann", Rank = 1, Secret = "s1", Salary = 10 }, new Account { Name = "bob", Rank = 1, Secret = "s2", Salary = 20 },
            new Account { Name = "cat", Rank = 2, Secret = "s3", Salary = 30 }, new Account { Name = "dan", Rank = 2, Secret = "s4", Salary = 40 },
            new Account { Name = "eve", Rank = 2, Secret = "s5", Salary = 50 });
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

    private static JsonElement Accounts(string json) => JsonDocument.Parse(json).RootElement.GetProperty("data").GetProperty("accounts");

    private static string[] Names(string json)
        => Accounts(json).GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("name").GetString()!).ToArray();

    // ---- the schema ----

    [Fact]
    public async Task The_input_types_name_the_columns_the_output_type_shows_and_no_others()
    {
        await using var sp = await BuildAsync();
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);
        var sdl = executor.Schema.ToString();

        var filter = Block(sdl, "input AccountFilterInput");
        var sort = Block(sdl, "input AccountSortInput");
        foreach (var column in new[] { "name:", "rank:", "salary:", "id:" })
        {
            Assert.Contains(column, filter);
            Assert.Contains(column, sort);
        }
        Assert.DoesNotContain("secret", filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sort, StringComparison.OrdinalIgnoreCase);
        // Hidden from the output type by attribute: hidden here too.
        Assert.DoesNotContain("memo", filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memo", sort, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memo", Block(sdl, "type Account "), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_columns_are_the_plain_readable_ones_and_follow_the_output_types_hiding()
    {
        var columns = IyuGraphQLQueryArguments.Columns(typeof(ColumnShapes), (_, _) => false).Select(c => c.Name).ToArray();

        Assert.Equal(["Plain", "Optional", "When", "Which"], columns);
    }

    private static string Block(string sdl, string header)
    {
        var start = sdl.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{header}' in the schema");
        return sdl[start..sdl.IndexOf("\n}", start, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task A_host_that_does_not_turn_it_on_has_the_schema_it_always_had()
    {
        await using var sp = await BuildAsync(whereAndOrder: false);
        var executor = await sp.GetRequestExecutorAsync(schemaName: null!, CancellationToken.None);

        var sdl = executor.Schema.ToString();

        Assert.DoesNotContain("AccountFilterInput", sdl);
        Assert.DoesNotContain("AccountSortInput", sdl);
        Assert.DoesNotContain("where:", sdl);
    }

    // ---- what it does for a caller who may read everything ----

    [Fact]
    public async Task A_condition_narrows_the_rows_and_the_total()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, SalaryPolicy);

        var json = await RunAsync(sp, "{ accounts(where: { rank: { eq: 2 } }) { totalCount nodes { name } } }");

        Assert.Equal(3, Accounts(json).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, Names(json).Length);   // the page is still the page
        Assert.All(Names(json), n => Assert.Contains(n, new[] { "cat", "dan", "eve" }));
    }

    [Fact]
    public async Task An_ordering_with_ties_is_a_stable_slice_every_row_exactly_once()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, SalaryPolicy);
        var seen = new List<(string Name, int Rank)>();
        string? after = null;

        for (var page = 0; page < 10; page++)
        {
            var json = await RunAsync(sp,
                "query($after: String) { accounts(first: 2, after: $after, order: { rank: DESC }) { nodes { name rank } pageInfo { endCursor hasNextPage } } }",
                new() { ["after"] = after });
            var accounts = Accounts(json);
            seen.AddRange(accounts.GetProperty("nodes").EnumerateArray().Select(n => (n.GetProperty("name").GetString()!, n.GetProperty("rank").GetInt32())));
            if (!accounts.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean()) break;
            after = accounts.GetProperty("pageInfo").GetProperty("endCursor").GetString();
        }

        Assert.Equal(["ann", "bob", "cat", "dan", "eve"], seen.Select(s => s.Name).Order().ToArray());
        Assert.Equal(seen.Select(s => s.Rank).OrderDescending().ToArray(), seen.Select(s => s.Rank).ToArray());
    }

    [Fact]
    public async Task Without_an_ordering_the_rows_come_in_key_order_as_before()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, SalaryPolicy);

        var json = await RunAsync(sp, "{ accounts(first: 3) { nodes { id } } }");

        var ids = Accounts(json).GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(ids.Order().ToArray(), ids);
    }

    // ---- the read rule: a column the caller may not read cannot be asked about ----

    public static TheoryData<string, string?> WaysToAskAboutTheSalary => new()
    {
        { "{ accounts(where: { salary: { gt: 25 } }) { nodes { name } } }", null },
        { "{ accounts(order: { salary: DESC }) { nodes { name } } }", null },
        { "{ accounts(where: { and: [{ rank: { eq: 2 } }, { or: [{ name: { eq: \"x\" } }, { salary: { gt: 0 } }] }] }) { nodes { name } } }", null },
        { "{ accounts(order: [{ rank: ASC }, { salary: DESC }]) { nodes { name } } }", null },
        { "{ a: accounts(where: { salary: { gt: 0 } }) { totalCount } b: accounts(order: { salary: ASC }) { totalCount } }", null },
        { "{ accounts(where: { salary: { gt: 0 } }) { totalCount } }", null },
        { "{ ...F } fragment F on Query { accounts(order: { salary: ASC }) { nodes { name } } }", null },
        { "query($w: AccountFilterInput) { accounts(where: $w) { nodes { name } } }", "w" },
        { "query($w: AccountFilterInput!) { accounts(where: { and: [$w] }) { nodes { name } } }", "w" },
        { "query($o: [AccountSortInput!]) { accounts(order: $o) { nodes { name } } }", "o" },
        { "query($v: Int) { accounts(where: { salary: { gt: $v } }) { nodes { name } } }", "v" },
        // A variable the request does not supply keeps its default — which names the column all the same.
        { "query($w: AccountFilterInput = { salary: { gt: 25 } }) { accounts(where: $w) { nodes { name } } }", null },
        { "query($v: Int = 25) { accounts(where: { salary: { gt: $v } }) { nodes { name } } }", null },
        { "query($o: [AccountSortInput!] = [{ salary: DESC }]) { accounts(order: $o) { nodes { name } } }", null },
    };

    private static Dictionary<string, object?>? VariablesFor(string? name) => name switch
    {
        "w" => new() { ["w"] = new Dictionary<string, object?> { ["salary"] = new Dictionary<string, object?> { ["gt"] = 25 } } },
        "o" => new() { ["o"] = new[] { new Dictionary<string, object?> { ["salary"] = "DESC" } } },
        "v" => new() { ["v"] = 25 },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(WaysToAskAboutTheSalary))]
    public async Task A_caller_who_may_not_read_a_column_cannot_filter_or_sort_by_it_and_no_query_runs(string query, string? variable)
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp);   // signed in with no permission at all

        var json = await RunAsync(sp, query, VariablesFor(variable));

        Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"nodes\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    [Theory]
    [MemberData(nameof(WaysToAskAboutTheSalary))]
    public async Task The_same_request_from_a_caller_who_may_read_it_is_answered(string query, string? variable)
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, SalaryPolicy);

        var json = await RunAsync(sp, query, VariablesFor(variable));

        Assert.DoesNotContain("\"errors\"", json, StringComparison.Ordinal);
        Assert.True(Volatile.Read(ref _counting.Commands) > 0);
    }

    [Fact]
    public async Task A_column_without_a_policy_is_open_to_a_caller_with_no_permissions()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp);

        var json = await RunAsync(sp, "{ accounts(where: { name: { eq: \"cat\" } }, order: { rank: ASC }) { totalCount nodes { name } } }");

        Assert.Equal(1, Accounts(json).GetProperty("totalCount").GetInt32());
        Assert.Equal(["cat"], Names(json));
    }

    [Theory]
    [InlineData("{ accounts(where: { salary: null }) { nodes { name } } }")]
    [InlineData("{ accounts(where: { salary: { eq: null } }) { nodes { name } } }")]
    public async Task Naming_a_column_is_enough_whatever_the_value_null_is_still_a_question_about_it(string query)
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp);

        var json = await RunAsync(sp, query);

        Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    [Theory]
    [InlineData("{ accounts(where: null) { totalCount } }")]
    [InlineData("{ accounts(order: null) { totalCount } }")]
    [InlineData("{ accounts(where: {}, order: []) { totalCount } }")]
    public async Task A_condition_that_names_no_column_names_nothing_to_refuse(string query)
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp);

        var json = await RunAsync(sp, query);

        Assert.Equal(5, Accounts(json).GetProperty("totalCount").GetInt32());
    }

    // ---- the size of what a condition may be ----

    [Fact]
    public async Task A_condition_nested_past_the_bound_is_refused_even_when_a_variable_carries_it_and_no_query_runs()
    {
        await using var sp = await BuildAsync(configure: g => g.MaxConditionDepth = 3);
        SetCurrentUser(sp, SalaryPolicy);
        object? condition = new Dictionary<string, object?> { ["name"] = new Dictionary<string, object?> { ["eq"] = "x" } };
        for (var i = 0; i < 6; i++) condition = new Dictionary<string, object?> { ["and"] = new[] { condition } };

        var json = await RunAsync(sp, "query($w: AccountFilterInput) { accounts(where: $w) { nodes { name } } }", new() { ["w"] = condition });

        Assert.Contains("\"IYU_CONDITION_REFUSED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    [Fact]
    public async Task A_long_list_inside_an_operation_counts_toward_the_bound_and_no_query_runs()
    {
        await using var sp = await BuildAsync();   // the default bound: 200 parts
        SetCurrentUser(sp, SalaryPolicy);
        var ids = Enumerable.Range(0, 500).Select(i => (object?)i).ToArray();

        var json = await RunAsync(sp, "query($ranks: [Int!]) { accounts(where: { rank: { in: $ranks } }) { totalCount } }", new() { ["ranks"] = ids });

        Assert.Contains("\"IYU_CONDITION_REFUSED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }

    [Fact]
    public async Task A_short_list_inside_an_operation_is_answered()
    {
        await using var sp = await BuildAsync();
        SetCurrentUser(sp, SalaryPolicy);

        var json = await RunAsync(sp, "{ accounts(where: { rank: { in: [1, 2] } }) { totalCount } }");

        Assert.Equal(5, Accounts(json).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_condition_with_too_many_parts_is_refused_and_no_query_runs()
    {
        await using var sp = await BuildAsync(configure: g => g.MaxConditionNodes = 10);
        SetCurrentUser(sp, SalaryPolicy);
        var parts = Enumerable.Range(0, 30).Select(i => new Dictionary<string, object?> { ["rank"] = new Dictionary<string, object?> { ["eq"] = i } }).ToArray();

        var json = await RunAsync(sp, "query($w: AccountFilterInput) { accounts(where: $w) { nodes { name } } }",
            new() { ["w"] = new Dictionary<string, object?> { ["or"] = parts } });

        Assert.Contains("\"IYU_CONDITION_REFUSED\"", json, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _counting.Commands));
    }
}
