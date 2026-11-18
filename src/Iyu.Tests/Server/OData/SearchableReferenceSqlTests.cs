using System.Linq.Expressions;
using Iyu.Data;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.UriParser;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// What a database is asked when <c>$search</c> reaches into a reference. A left join makes the database read the referenced
/// row of every candidate before it filters, orders or pages; a correlated <c>EXISTS</c> lets it test the reference only where the
/// row's own text did not already match — and gives the same rows.
/// </summary>
public sealed class SearchableReferenceSqlTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SearchableReferenceSqlTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private RefContext NewContext()
    {
        var context = new RefContext(new DbContextOptionsBuilder<RefContext>().UseSqlite(_connection).Options);
        context.Database.EnsureCreated();
        return context;
    }

    private static Expression<Func<RefOrderExt, bool>> Bind(string term, IyuDbContext? requestContext)
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<RefOrderExt, RefOrder>("RefOrders");
        builder.AddEntityPair<RefCustomerExt, RefCustomer>("RefCustomers");
        var context = new QueryBinderContext(builder.GetEdmModel(), new ODataQuerySettings(), typeof(RefOrderExt));

        var http = new DefaultHttpContext();
        if (requestContext is not null)
            http.RequestServices = new ServiceCollection().AddSingleton(requestContext).BuildServiceProvider();
        var binder = new IyuStringSearchBinder(builder.Registry, new HttpContextAccessor { HttpContext = http });

        return (Expression<Func<RefOrderExt, bool>>)binder.BindSearch(new SearchClause(new SearchTermNode(term)), context);
    }

    private static void Seed(RefContext context)
    {
        var acme = new RefCustomerExt { Id = Guid.NewGuid(), Name = "ACME Corp" };
        var globex = new RefCustomerExt { Id = Guid.NewGuid(), Name = "Globex" };
        context.CustomersExt.AddRange(acme, globex);
        context.OrdersExt.AddRange(
            new RefOrderExt { Id = Guid.NewGuid(), Note = "by reference", CustomerId = acme.Id },
            new RefOrderExt { Id = Guid.NewGuid(), Note = "other customer", CustomerId = globex.Id },
            new RefOrderExt { Id = Guid.NewGuid(), Note = "acme in own text", CustomerId = globex.Id },
            new RefOrderExt { Id = Guid.NewGuid(), Note = "no reference at all" });
        context.SaveChanges();
    }

    [Fact]
    public void A_reference_the_model_maps_is_tested_by_a_correlated_exists_not_a_left_join()
    {
        using var context = NewContext();

        var sql = context.OrdersExt.Where(Bind("acme", context)).ToQueryString();

        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN", sql, StringComparison.Ordinal);
    }

    /// <summary>An endpoint outside OData (an export, a bulk action) gets the same shape when it says which context it runs on.</summary>
    [Fact]
    public async Task The_endpoint_side_filter_uses_exists_when_given_its_context_and_a_join_when_not()
    {
        using var context = NewContext();
        Seed(context);
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<RefOrderExt, RefOrder>("RefOrders");
        builder.AddEntityPair<RefCustomerExt, RefCustomer>("RefCustomers");
        var search = new IyuSearch(builder.Registry);
        var caller = new System.Security.Claims.ClaimsPrincipal();

        var viaContext = await search.CreateFilterAsync<RefOrderExt>("acme", caller, context);
        var plain = await search.CreateFilterAsync<RefOrderExt>("acme", caller);

        Assert.Contains("EXISTS", context.OrdersExt.Where(viaContext.Predicate).ToQueryString(), StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN", context.OrdersExt.Where(plain.Predicate).ToQueryString(), StringComparison.Ordinal);
        Assert.Equal(
            context.OrdersExt.Where(plain.Predicate).Select(o => o.Note).OrderBy(n => n).ToList(),
            context.OrdersExt.Where(viaContext.Predicate).Select(o => o.Note).OrderBy(n => n).ToList());
    }

    [Fact]
    public void Without_a_context_the_reference_is_still_reached_as_a_left_join()
    {
        using var context = NewContext();

        var sql = context.OrdersExt.Where(Bind("acme", requestContext: null)).ToQueryString();

        Assert.Contains("LEFT JOIN", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_shapes_select_the_same_rows()
    {
        using var context = NewContext();
        Seed(context);

        var withExists = context.OrdersExt.Where(Bind("acme", context)).Select(o => o.Note).OrderBy(n => n).ToList();
        var withJoin = context.OrdersExt.Where(Bind("acme", requestContext: null)).Select(o => o.Note).OrderBy(n => n).ToList();

        Assert.Equal(["acme in own text", "by reference"], withExists);
        Assert.Equal(withJoin, withExists);
    }

    /// <summary>A row with no referenced row is not found through the reference, and a negated search keeps it — as the join did.</summary>
    [Fact]
    public void A_row_without_a_reference_is_not_found_by_it_and_is_kept_by_its_negation()
    {
        using var context = NewContext();
        Seed(context);

        var found = context.OrdersExt.Where(Bind("acme", context)).Select(o => o.Note).ToList();
        Assert.DoesNotContain("no reference at all", found);

        static Expression<Func<RefOrderExt, bool>> Negate(Expression<Func<RefOrderExt, bool>> search)
            => Expression.Lambda<Func<RefOrderExt, bool>>(Expression.Not(search.Body), search.Parameters);

        var kept = context.OrdersExt.Where(Negate(Bind("acme", context))).Select(o => o.Note).OrderBy(n => n).ToList();
        var keptByJoin = context.OrdersExt.Where(Negate(Bind("acme", requestContext: null))).Select(o => o.Note).OrderBy(n => n).ToList();

        Assert.Contains("no reference at all", kept);
        Assert.Equal(keptByJoin, kept);
    }

    /// <summary>The expression carries the context it was built with; a second request with its own context must not reuse the first.</summary>
    [Fact]
    public void Two_requests_with_their_own_contexts_each_answer_from_their_own()
    {
        using var first = NewContext();
        Seed(first);
        using var second = NewContext();   // the same in-memory database through the same connection

        var one = first.OrdersExt.Where(Bind("globex", first)).Count();
        var two = second.OrdersExt.Where(Bind("globex", second)).Count();

        Assert.Equal(2, one);
        Assert.Equal(one, two);
    }

    [Fact]
    public void A_context_whose_model_does_not_know_the_element_falls_back_to_the_left_join()
    {
        using var other = new UnrelatedContext(new DbContextOptionsBuilder<UnrelatedContext>().UseSqlite(_connection).Options);
        using var context = NewContext();

        var lambda = Bind("acme", other);

        Assert.Contains("LEFT JOIN", context.OrdersExt.Where(lambda).ToQueryString(), StringComparison.Ordinal);
    }

    private sealed class UnrelatedContext(DbContextOptions<UnrelatedContext> options) : IyuDbContext(options)
    {
        public DbSet<RefCustomer> Customers => Set<RefCustomer>();
    }
}
