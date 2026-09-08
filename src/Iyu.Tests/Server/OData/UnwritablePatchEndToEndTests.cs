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

// Top-level public types for the reason ODataTestServerRoutingTests documents:
// a nested controller is not IsPublic and MVC's ControllerFeatureProvider skips it.

/// <summary>Write (table) side — it has no counterpart for the derived column.</summary>
public sealed class LedgerEntry : IyuEntity
{
    public string Memo { get; set; } = "";
}

/// <summary>Read (view) side. <c>ItemCount</c> is what a view computes and no table stores.</summary>
public sealed class LedgerEntryExt : IyuEntity
{
    public string Memo { get; set; } = "";
    public int ItemCount { get; set; }
}

public sealed class LedgerContext(DbContextOptions<LedgerContext> options) : IyuDbContext(options)
{
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();
    public DbSet<LedgerEntryExt> EntriesExt => Set<LedgerEntryExt>();
}

public sealed class LedgerEntriesController(LedgerContext ctx)
    : IyuODataController<LedgerEntryExt, LedgerEntry>(ctx);

/// <summary>
/// What the generic write path answers when the properties a caller sent cannot be stored,
/// measured over HTTP rather than against the controller in isolation.
/// </summary>
/// <remarks>
/// <para>
/// The distinction only exists on a pair whose read and write types are <b>different
/// classes</b>: a property declared by the view and absent from the table is the ordinary
/// derived column, and it is the one a caller most plausibly tries to write. Registering a
/// pair as <c>&lt;T, T&gt;</c> cannot produce the case at all.
/// </para>
/// <para>
/// Going over HTTP is what separates the two refusals below. A property the model never
/// declares is rejected by deserialization, before any of this controller's logic runs;
/// one the read model declares binds successfully and is refused here. Both are 400, and
/// only the second can say which property and why — which is the difference a caller acts on.
/// </para>
/// </remarks>
public class UnwritablePatchEndToEndTests
{
    private const string Set = "LedgerEntries";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "ledger-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<LedgerContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(LedgerEntriesController).Assembly);
                options.ODataModel.AddEntityPair<LedgerEntryExt, LedgerEntry>(Set);
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<(WebApplication App, Guid Id)> StartWithRowAsync()
    {
        var app = await StartAsync();
        var id = Guid.NewGuid();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerContext>();
        db.Entries.Add(new LedgerEntry { Id = id, Memo = "original" });
        await db.SaveChangesAsync();
        return (app, id);
    }

    private static LedgerEntry? Row(WebApplication app, Guid id)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LedgerContext>()
            .Entries.AsNoTracking().FirstOrDefault(e => e.Id == id);
    }

    /// <summary>
    /// An update carrying only a derived property is refused, and the body names the property
    /// and says it is derived — the two facts a caller cannot otherwise tell apart from a lock.
    /// </summary>
    [Fact]
    public async Task An_update_of_only_a_derived_property_is_refused_and_says_why()
    {
        var (app, id) = await StartWithRowAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient()
                .PatchAsJsonAsync($"/$data/{Set}({id})", new { ItemCount = 99 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(nameof(LedgerEntryExt.ItemCount), body, StringComparison.Ordinal);
            Assert.Contains("derived", body, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("original", Row(app, id)?.Memo);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// The same object sent back whole still applies the field that changed. This is why the
    /// refusal is scoped to an update in which <i>every</i> property is unwritable.
    /// </summary>
    [Fact]
    public async Task An_update_carrying_a_derived_property_beside_a_writable_one_succeeds()
    {
        var (app, id) = await StartWithRowAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient()
                .PatchAsJsonAsync($"/$data/{Set}({id})", new { ItemCount = 99, Memo = "changed" });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("changed", Row(app, id)?.Memo);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// A property the model does not declare at all never reaches this controller — the
    /// deserializer refuses it. Pinned because it is the case most easily assumed to be the
    /// silent one, and it never was.
    /// </summary>
    [Fact]
    public async Task A_property_the_model_never_declares_is_refused_by_deserialization()
    {
        var (app, id) = await StartWithRowAsync();
        try
        {
            using var response = await app.GetTestServer().CreateClient()
                .PatchAsJsonAsync($"/$data/{Set}({id})", new { Memoo = "typo" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("original", Row(app, id)?.Memo);
        }
        finally { await app.DisposeAsync(); }
    }
}
