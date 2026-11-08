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
/// A search tests the text of every property of the rows it matches, so the answer says whether a guess is right.
/// <see cref="IyuSearch"/> gives an endpoint outside OData the list's rule, and the list's rule refuses a caller who does
/// not satisfy a property policy of the searched set — the helper must not be the way around it.
/// </summary>
public class IyuSearchPropertyPolicyTests
{
    private const string NotesRead = "notes.read";

    private static readonly ClaimsPrincipal Reader = new(new ClaimsIdentity([new Claim("perm", NotesRead)], "Test"));
    private static readonly ClaimsPrincipal Anyone = new(new ClaimsIdentity([new Claim("perm", "anyone")], "Test"));

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "iyu-search-prop-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(NotesRead, p => p.RequireClaim("perm", NotesRead)));
        builder.Services.AddIyuMainServer<RefContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(RefOrdersController).Assembly);
                options.ODataModel.AddEntityPair<RefOrderExt, RefOrder>("RefOrders");
                options.ODataModel.AddEntityPair<RefCustomerExt, RefCustomer>("RefCustomers");
                options.AuthorizeProperty<RefOrderExt>(x => x.Note, NotesRead);
            });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<RefContext>();
        ctx.OrdersExt.Add(new RefOrderExt { Id = Guid.NewGuid(), Note = "a-note-behind-the-policy" });
        await ctx.SaveChangesAsync();
        return app;
    }

    [Fact]
    public async Task A_caller_who_fails_a_property_policy_of_the_searched_set_is_refused()
    {
        var app = await StartAsync();
        try
        {
            using var scope = app.Services.CreateScope();
            var search = scope.ServiceProvider.GetRequiredService<IyuSearch>();
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => search.CreateFilterAsync<RefOrderExt>("behind", Anyone));
            Assert.Contains(NotesRead, error.Message, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_caller_who_holds_the_policy_searches_as_before()
    {
        var app = await StartAsync();
        try
        {
            using var scope = app.Services.CreateScope();
            var filter = await scope.ServiceProvider.GetRequiredService<IyuSearch>()
                .CreateFilterAsync<RefOrderExt>("behind", Reader);
            var ctx = scope.ServiceProvider.GetRequiredService<RefContext>();
            Assert.Single(await ctx.OrdersExt.Where(filter.Predicate).ToListAsync());
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A blank search selects every row and tests nothing, so there is nothing to refuse.</summary>
    [Fact]
    public async Task A_blank_search_tests_no_text_and_is_not_refused()
    {
        var app = await StartAsync();
        try
        {
            using var scope = app.Services.CreateScope();
            var filter = await scope.ServiceProvider.GetRequiredService<IyuSearch>()
                .CreateFilterAsync<RefOrderExt>("  ", Anyone);
            var ctx = scope.ServiceProvider.GetRequiredService<RefContext>();
            Assert.Single(await ctx.OrdersExt.Where(filter.Predicate).ToListAsync());
        }
        finally { await app.DisposeAsync(); }
    }
}
