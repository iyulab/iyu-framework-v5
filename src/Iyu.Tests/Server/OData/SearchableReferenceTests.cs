using System.Linq.Expressions;
using System.Net;
using Iyu.Core.Attributes;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.UriParser;
using Xunit;

namespace Iyu.Tests.Server.OData;

public sealed class RefCustomer : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class RefCustomerExt : IyuEntity
{
    [Searchable] public string Name { get; set; } = "";
    public string? Secret { get; set; }
}

public sealed class RefOrder : IyuEntity
{
    public string Note { get; set; } = "";
    public Guid? CustomerId { get; set; }
}

public sealed class RefOrderExt : IyuEntity
{
    [Searchable] public string Note { get; set; } = "";
    public Guid? CustomerId { get; set; }
    [Searchable] public RefCustomerExt? Customer { get; set; }
}

public sealed class RefContext(DbContextOptions<RefContext> options) : IyuDbContext(options)
{
    public DbSet<RefOrder> Orders => Set<RefOrder>();
    public DbSet<RefOrderExt> OrdersExt => Set<RefOrderExt>();
    public DbSet<RefCustomer> Customers => Set<RefCustomer>();
    public DbSet<RefCustomerExt> CustomersExt => Set<RefCustomerExt>();
}

public sealed class RefOrdersController(RefContext ctx) : IyuODataController<RefOrderExt, RefOrder>(ctx);
public sealed class RefCustomersController(RefContext ctx) : IyuODataController<RefCustomerExt, RefCustomer>(ctx);

/// <summary>
/// A <c>[Searchable]</c> reference makes <c>$search</c> match the referenced row's searched text — a list shown with
/// its reference's name is found by that name. One step, a served type only, and never past the caller's read policy.
/// </summary>
public class SearchableReferenceTests
{
    private static Func<RefOrderExt, bool> Bind(
        string term, bool registerCustomers, bool protectCustomers = false, ISet<string>? allowed = null, bool withRequest = true)
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<RefOrderExt, RefOrder>("RefOrders");
        if (registerCustomers) builder.AddEntityPair<RefCustomerExt, RefCustomer>("RefCustomers");
        if (protectCustomers) builder.RestrictPolicy("RefCustomers", readPolicy: "customers.read");
        var context = new QueryBinderContext(builder.GetEdmModel(), new ODataQuerySettings(), typeof(RefOrderExt));

        var accessor = new HttpContextAccessor { HttpContext = withRequest ? new DefaultHttpContext() : null };
        if (allowed is not null)
            accessor.HttpContext!.Items[IyuStringSearchBinder.AllowedNavigationsItem] = new HashSet<string>(allowed);

        var binder = new IyuStringSearchBinder(builder.Registry, accessor);
        var lambda = Assert.IsAssignableFrom<LambdaExpression>(
            binder.BindSearch(new SearchClause(new SearchTermNode(term)), context));
        return (Func<RefOrderExt, bool>)lambda.Compile();
    }

    private static RefOrderExt OrderOf(string customer)
        => new() { Note = "plain", Customer = new RefCustomerExt { Name = customer, Secret = "acme-secret" } };

    [Fact]
    public void A_searchable_reference_matches_on_the_referenced_rows_searched_text()
    {
        var predicate = Bind("acme", registerCustomers: true);

        Assert.True(predicate(OrderOf("ACME Corp")));
        Assert.True(predicate(new RefOrderExt { Note = "acme note" }));      // own text still searched
        Assert.False(predicate(new RefOrderExt { Note = "plain" }));          // no reference — not a match, no fault
    }

    [Fact]
    public void Only_the_referenced_types_searched_properties_count()
        => Assert.False(Bind("acme", registerCustomers: true)(OrderOf("Other")));   // Secret is not declared searchable

    [Fact]
    public void A_reference_to_a_type_no_set_serves_is_not_searched()
        => Assert.False(Bind("acme", registerCustomers: false)(OrderOf("ACME Corp")));

    /// <summary>Deny by default: a reference to a protected set is searched only when the request allows it.</summary>
    [Fact]
    public void A_reference_to_a_protected_set_is_not_searched_unless_the_request_allows_it()
    {
        Assert.False(Bind("acme", registerCustomers: true, protectCustomers: true)(OrderOf("ACME Corp")));
        Assert.False(Bind("acme", registerCustomers: true, protectCustomers: true, withRequest: false)(OrderOf("ACME Corp")));
        Assert.True(Bind("acme", registerCustomers: true, protectCustomers: true,
            allowed: new HashSet<string> { "Customer" })(OrderOf("ACME Corp")));
    }

    // ---- through the host: the caller's read policy on the referenced set decides ----

    private const string CustomersRead = "customers.read";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "ref-search-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(CustomersRead, p => p.RequireClaim("perm", CustomersRead)));
        builder.Services.AddIyuMainServer<RefContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(RefOrdersController).Assembly);
                options.ODataModel.AddEntityPair<RefOrderExt, RefOrder>("RefOrders");
                options.ODataModel.AddEntityPair<RefCustomerExt, RefCustomer>("RefCustomers");
                options.Authorize<RefCustomerExt>(read: CustomersRead);
            });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<RefContext>();
        var customerId = Guid.NewGuid();
        ctx.CustomersExt.Add(new RefCustomerExt { Id = customerId, Name = "ACME Corp" });
        ctx.OrdersExt.Add(new RefOrderExt { Id = Guid.NewGuid(), Note = "plain", CustomerId = customerId });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Excluded)> SearchAsync(
        WebApplication app, string? perm, string query = "$search=acme")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/$data/RefOrders?" + query);
        req.Headers.Add("X-Test-Perm", perm ?? "anyone");
        using var resp = await app.GetTestServer().CreateClient().SendAsync(req);
        var excluded = resp.Headers.TryGetValues("Iyu-Search-Excluded", out var v) ? string.Join(",", v) : null;
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(), excluded);
    }

    [Fact]
    public async Task A_caller_who_may_read_the_referenced_set_finds_the_row_by_its_name()
    {
        var app = await StartAsync();
        try
        {
            var (status, body, excluded) = await SearchAsync(app, CustomersRead);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"plain\"", body, StringComparison.Ordinal);
            Assert.Null(excluded);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_caller_who_may_not_gets_no_match_through_it_and_is_told_which_reference_was_left_out()
    {
        var app = await StartAsync();
        try
        {
            var (status, body, excluded) = await SearchAsync(app, perm: null);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain("\"plain\"", body, StringComparison.Ordinal);
            Assert.Equal("Customer", excluded);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The search option without its dollar sign reaches the same binder — and the same denial.</summary>
    [Fact]
    public async Task The_dollar_less_search_option_is_held_to_the_same_policy()
    {
        var app = await StartAsync();
        try
        {
            var (status, body, excluded) = await SearchAsync(app, perm: null, query: "search=acme");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain("\"plain\"", body, StringComparison.Ordinal);
            Assert.Equal("Customer", excluded);
        }
        finally { await app.DisposeAsync(); }
    }
}
