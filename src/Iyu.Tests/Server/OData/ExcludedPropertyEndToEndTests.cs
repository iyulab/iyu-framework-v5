using System.Net;
using System.Net.Http.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the same reason ODataTestServerRoutingTests documents:
// a nested controller is not IsPublic and MVC's ControllerFeatureProvider skips it.

/// <summary>Write (table) side of the pair — carries the value that must never leave the server.</summary>
public sealed class GatedAccount : IyuEntity
{
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

/// <summary>Read (view) side. A distinct type from the write side, which is the shape that matters here.</summary>
public sealed class GatedAccountExt : IyuEntity
{
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class GatedAccountContext(DbContextOptions<GatedAccountContext> options) : IyuDbContext(options)
{
    public DbSet<GatedAccount> Accounts => Set<GatedAccount>();
    public DbSet<GatedAccountExt> AccountsExt => Set<GatedAccountExt>();
}

public sealed class GatedAccountsController(GatedAccountContext ctx)
    : IyuODataController<GatedAccountExt, GatedAccount>(ctx);

/// <summary>
/// What an excluded property does over HTTP, on a pair whose read and write types are
/// <b>different classes</b>.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests around <see cref="IyuEdmModelBuilder"/> register a pair as
/// <c>&lt;T, T&gt;</c>, so they cannot see which of the two types carries the exclusion —
/// and that is exactly what the feature's guidance got wrong. Here the two are distinct,
/// so "exclude the read type" is a claim the test can actually fail.
/// </para>
/// <para>
/// The write half is the half that was never covered: request bodies bind to the
/// <i>read</i> type, so removing the property from the model is what makes a
/// <c>POST</c>/<c>PATCH</c> naming it fail. Asserting the EDM alone would not have shown
/// that a known hash could otherwise be planted through the generic write path — and a
/// hash that can be written is a password that can be chosen.
/// </para>
/// </remarks>
public class ExcludedPropertyEndToEndTests
{
    private const string Set = "GatedAccounts";
    private const string Sentinel = "SENTINEL-HASH-MUST-NOT-LEAK";

    private static async Task<WebApplication> StartAsync(bool exclude)
    {
        var dbName = "gated-" + Guid.NewGuid().ToString("N"); // one database per app, shared by every scope
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<GatedAccountContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(GatedAccountsController).Assembly);
                options.ODataModel.AddEntityPair<GatedAccountExt, GatedAccount>(Set);
                if (exclude) options.ODataModel.Exclude<GatedAccountExt>(x => x.PasswordHash);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static GatedAccount? Row(WebApplication app, Guid id)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<GatedAccountContext>()
            .Accounts.AsNoTracking().FirstOrDefault(a => a.Id == id);
    }

    /// <summary>
    /// The control. Without the exclusion a known hash can be planted through the generic
    /// write path and read straight back out — so every assertion below is about the
    /// exclusion and not about some other guard that happened to be in the way.
    /// </summary>
    /// <remarks>
    /// The two halves touch different sets on purpose: in a deployment the read type is a
    /// view over the write table, and the in-memory provider has no views, so the write is
    /// asserted against the stored row and the read against a seeded read row.
    /// </remarks>
    [Fact]
    public async Task Without_the_exclusion_the_value_is_writable_and_readable()
    {
        var app = await StartAsync(exclude: false);
        try
        {
            var client = app.GetTestServer().CreateClient();
            var id = Guid.NewGuid();

            // A hash that can be written is a password that can be chosen.
            using var post = await client.PostAsJsonAsync($"/$data/{Set}",
                new { Id = id, Login = "a", PasswordHash = "PLANTED" });
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            Assert.Equal("PLANTED", Row(app, id)?.PasswordHash);

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GatedAccountContext>();
                db.AccountsExt.Add(new GatedAccountExt { Id = Guid.NewGuid(), Login = "a", PasswordHash = Sentinel });
                await db.SaveChangesAsync();
            }

            using var read = await client.GetAsync($"/$data/{Set}?$select={nameof(GatedAccountExt.PasswordHash)}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Contains(Sentinel, await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <remarks>
    /// The assertion is about the stored <i>value</i>, not the property name: an OData error
    /// echoes the term the caller wrote, which carries nothing the caller did not already
    /// know. What must never appear is the hash itself — including through the plain list
    /// read, which is the route a caller takes when the explicit ones start failing.
    /// </remarks>
    [Theory]
    [InlineData("$select=PasswordHash", HttpStatusCode.BadRequest)]
    [InlineData("$filter=startswith(PasswordHash,'S')", HttpStatusCode.BadRequest)]   // probing one character at a time
    [InlineData("$orderby=PasswordHash", HttpStatusCode.BadRequest)]
    [InlineData("", HttpStatusCode.OK)]                                               // the unfiltered read still works...
    public async Task Reading_an_excluded_property_is_rejected_and_never_returns_the_value(
        string query, HttpStatusCode expected)
    {
        var app = await StartAsync(exclude: true);
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GatedAccountContext>();
                db.AccountsExt.Add(new GatedAccountExt { Id = Guid.NewGuid(), Login = "a", PasswordHash = Sentinel });
                await db.SaveChangesAsync();
            }

            using var response = await app.GetTestServer().CreateClient().GetAsync($"/$data/{Set}?{query}");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(expected, response.StatusCode);
            Assert.DoesNotContain(Sentinel, body, StringComparison.Ordinal);   // ...and never carries the value
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A body naming the excluded property is refused, and nothing is written.</summary>
    [Fact]
    public async Task Posting_an_excluded_property_is_rejected_and_stores_nothing()
    {
        var app = await StartAsync(exclude: true);
        try
        {
            var id = Guid.NewGuid();
            using var response = await app.GetTestServer().CreateClient()
                .PostAsJsonAsync($"/$data/{Set}", new { Id = id, Login = "a", PasswordHash = "PLANTED" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(Row(app, id));
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A partial update naming the excluded property is refused, and the stored value is untouched.</summary>
    [Fact]
    public async Task Patching_an_excluded_property_is_rejected_and_leaves_the_value_intact()
    {
        var app = await StartAsync(exclude: true);
        try
        {
            var id = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GatedAccountContext>();
                db.Accounts.Add(new GatedAccount { Id = id, Login = "a", PasswordHash = "ORIGINAL" });
                await db.SaveChangesAsync();
            }

            using var response = await app.GetTestServer().CreateClient()
                .PatchAsync($"/$data/{Set}({id})", JsonContent.Create(new { PasswordHash = "PLANTED" }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ORIGINAL", Row(app, id)?.PasswordHash);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The exclusion must not cost the rest of the entity: an ordinary write still works.</summary>
    [Fact]
    public async Task A_body_that_does_not_name_the_excluded_property_still_writes()
    {
        var app = await StartAsync(exclude: true);
        try
        {
            var id = Guid.NewGuid();
            using var response = await app.GetTestServer().CreateClient()
                .PostAsJsonAsync($"/$data/{Set}", new { Id = id, Login = "kept" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("kept", Row(app, id)?.Login);
        }
        finally { await app.DisposeAsync(); }
    }
}
