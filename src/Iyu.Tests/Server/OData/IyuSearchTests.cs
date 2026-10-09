using System.Security.Claims;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// <see cref="IyuSearch"/> gives an endpoint outside OData the rule a list's <c>$search</c> applies — the same grammar,
/// properties, reference step and policy exclusion — so «the rows this search shows» is one answer, not two.
/// </summary>
public class IyuSearchTests
{
    private const string CustomersRead = "customers.read";

    private static readonly ClaimsPrincipal Permitted =
        new(new ClaimsIdentity([new Claim("perm", CustomersRead)], "Test"));

    private static readonly ClaimsPrincipal Anyone = new(new ClaimsIdentity([new Claim("perm", "anyone")], "Test"));

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "iyu-search-" + Guid.NewGuid().ToString("N");
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
        var acme = Guid.NewGuid();
        ctx.CustomersExt.Add(new RefCustomerExt { Id = acme, Name = "ACME Corp" });
        ctx.OrdersExt.Add(new RefOrderExt { Id = Guid.NewGuid(), Note = "valve", CustomerId = acme });
        ctx.OrdersExt.Add(new RefOrderExt { Id = Guid.NewGuid(), Note = "pump" });
        ctx.OrdersExt.Add(new RefOrderExt { Id = Guid.NewGuid(), Note = "acme pump" });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static async Task<(List<string> Notes, IReadOnlyList<string> Excluded)> SelectAsync(
        WebApplication app, string? search, ClaimsPrincipal user)
    {
        using var scope = app.Services.CreateScope();
        var filter = await scope.ServiceProvider.GetRequiredService<IyuSearch>().CreateFilterAsync<RefOrderExt>(search, user);
        var ctx = scope.ServiceProvider.GetRequiredService<RefContext>();
        var notes = await ctx.OrdersExt.Where(filter.Predicate).Select(o => o.Note).OrderBy(n => n).ToListAsync();
        return (notes, filter.ExcludedNavigations);
    }

    [Fact]
    public async Task A_caller_who_may_read_the_referenced_set_selects_by_its_text()
    {
        var app = await StartAsync();
        try
        {
            var (notes, excluded) = await SelectAsync(app, "acme", Permitted);
            Assert.Equal(["acme pump", "valve"], notes);
            Assert.Empty(excluded);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_caller_who_may_not_selects_by_own_text_only_and_is_told_what_was_left_out()
    {
        var app = await StartAsync();
        try
        {
            var (notes, excluded) = await SelectAsync(app, "acme", Anyone);
            Assert.Equal(["acme pump"], notes);
            Assert.Equal(["Customer"], excluded);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The grammar is the list's — juxtaposed words are AND, and OR / NOT combine the same way.</summary>
    [Theory]
    [InlineData("acme pump", new[] { "acme pump" })]
    [InlineData("valve OR pump", new[] { "acme pump", "pump", "valve" })]
    [InlineData("pump NOT acme", new[] { "pump" })]
    [InlineData("\"acme pump\"", new[] { "acme pump" })]
    public async Task The_search_grammar_is_the_lists(string search, string[] expected)
    {
        var app = await StartAsync();
        try
        {
            Assert.Equal(expected, (await SelectAsync(app, search, Permitted)).Notes);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The same text through the list and through the service selects the same rows for the same caller.</summary>
    [Theory]
    [InlineData(CustomersRead)]
    [InlineData(null)]
    public async Task The_list_and_the_service_agree(string? perm)
    {
        var app = await StartAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/$data/RefOrders?$search=acme&$select=Note");
            req.Headers.Add("X-Test-Perm", perm ?? "anyone");
            using var resp = await app.GetTestServer().CreateClient().SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            var (notes, _) = await SelectAsync(app, "acme", perm is null ? Anyone : Permitted);
            foreach (var note in new[] { "valve", "pump", "acme pump" })
                Assert.Equal(notes.Contains(note), body.Contains($"\"{note}\"", StringComparison.Ordinal));
        }
        finally { await app.DisposeAsync(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task No_search_selects_every_row(string? search)
    {
        var app = await StartAsync();
        try
        {
            Assert.Equal(3, (await SelectAsync(app, search, Anyone)).Notes.Count);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task An_invalid_search_expression_is_an_argument_error()
    {
        var app = await StartAsync();
        try
        {
            var search = app.Services.GetRequiredService<IyuSearch>();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => search.CreateFilterAsync<RefOrderExt>("\"unterminated", Anyone));
            Assert.Equal("search", ex.ParamName);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>Without an authorization service nothing protected is allowed — denied by default, as in the list.</summary>
    [Fact]
    public async Task Without_authorization_no_protected_reference_is_allowed()
    {
        var builder = new IyuEdmModelBuilder();
        builder.AddEntityPair<RefOrderExt, RefOrder>("RefOrders");
        builder.AddEntityPair<RefCustomerExt, RefCustomer>("RefCustomers");
        builder.RestrictPolicy("RefCustomers", readPolicy: CustomersRead);

        var search = new IyuSearch(builder.Registry);
        Assert.Empty(await search.AllowedNavigationsAsync(typeof(RefOrderExt), Permitted));
        Assert.Equal(["Customer"], (await search.CreateFilterAsync<RefOrderExt>("acme", Permitted)).ExcludedNavigations);
    }
}
