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

/// <summary>Write (table) side of the pair — a value that must stay writable, and one that must not.</summary>
public sealed class TrackedOrder : IyuEntity
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
}

/// <summary>Read (view) side. A distinct type from the write side, which is the shape that matters here.</summary>
public sealed class TrackedOrderExt : IyuEntity
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class TrackedOrderContext(DbContextOptions<TrackedOrderContext> options) : IyuDbContext(options)
{
    public DbSet<TrackedOrder> Orders => Set<TrackedOrder>();
    public DbSet<TrackedOrderExt> OrdersExt => Set<TrackedOrderExt>();
}

public sealed class TrackedOrdersController(TrackedOrderContext ctx)
    : IyuODataController<TrackedOrderExt, TrackedOrder>(ctx);

/// <summary>
/// What <see cref="IyuEdmModelBuilder.ExcludeFromWrite{T}"/> does over HTTP, on a pair whose
/// read and write types are <b>different classes</b> — same rationale as
/// <see cref="ExcludedPropertyEndToEndTests"/> for why that distinction matters here.
/// </summary>
/// <remarks>
/// The defect this guards against is the one HD-55 (yesung, 2026-08-27) found in a consumer:
/// a domain state field (there, <c>Order.ProductionState</c>) legitimately needs to be
/// writable — by a dedicated transition endpoint that also logs the change — but the generic
/// OData PATCH surface let any client overwrite it directly, bypassing that log entirely.
/// <see cref="IyuEdmModelBuilder.Exclude{T}"/> cannot express "writable by someone, just not
/// by this generic path" — it removes the property from reads too. This is the feature that
/// closes only the generic write path while leaving reads (and a dedicated write path the
/// consumer builds itself, going straight to the write <c>DbSet</c>) untouched.
/// </remarks>
public class WriteExcludedPropertyEndToEndTests
{
    private const string Set = "TrackedOrders";

    private static async Task<WebApplication> StartAsync(bool excludeFromWrite)
    {
        var dbName = "tracked-" + Guid.NewGuid().ToString("N"); // one database per app, shared by every scope
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<TrackedOrderContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(TrackedOrdersController).Assembly);
                options.ODataModel.AddEntityPair<TrackedOrderExt, TrackedOrder>(Set);
                if (excludeFromWrite) options.ODataModel.ExcludeFromWrite<TrackedOrderExt>(x => x.Status);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static TrackedOrder? Row(WebApplication app, Guid id)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TrackedOrderContext>()
            .Orders.AsNoTracking().FirstOrDefault(o => o.Id == id);
    }

    /// <summary>The control — without the marker, the generic path writes the field like any other.</summary>
    [Fact]
    public async Task Without_the_marker_the_field_is_writable_through_post_and_patch()
    {
        var app = await StartAsync(excludeFromWrite: false);
        try
        {
            var client = app.GetTestServer().CreateClient();
            var id = Guid.NewGuid();

            using var post = await client.PostAsJsonAsync($"/$data/{Set}", new { Id = id, Name = "a", Status = "shipped" });
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            Assert.Equal("shipped", Row(app, id)?.Status);

            using var patch = await client.PatchAsync($"/$data/{Set}({id})", JsonContent.Create(new { Status = "delivered" }));
            Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
            Assert.Equal("delivered", Row(app, id)?.Status);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// POST naming the marked property still succeeds — unlike <see cref="ExcludedPropertyEndToEndTests"/>,
    /// this is a silent drop (the OData <c>Computed</c> contract), not a rejection — but the value
    /// itself never reaches the write row.
    /// </summary>
    [Fact]
    public async Task Posting_the_marked_property_succeeds_but_the_value_is_dropped()
    {
        var app = await StartAsync(excludeFromWrite: true);
        try
        {
            var id = Guid.NewGuid();
            using var response = await app.GetTestServer().CreateClient()
                .PostAsJsonAsync($"/$data/{Set}", new { Id = id, Name = "kept", Status = "PLANTED" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var row = Row(app, id);
            Assert.Equal("kept", row?.Name);       // the rest of the body still writes
            Assert.Equal("", row?.Status);          // the marked field falls back to the CLR default
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>PATCH naming the marked property leaves the stored value untouched, and still answers 204.</summary>
    [Fact]
    public async Task Patching_the_marked_property_leaves_the_stored_value_intact()
    {
        var app = await StartAsync(excludeFromWrite: true);
        try
        {
            var id = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TrackedOrderContext>();
                db.Orders.Add(new TrackedOrder { Id = id, Name = "a", Status = "ORIGINAL" });
                await db.SaveChangesAsync();
            }

            using var response = await app.GetTestServer().CreateClient()
                .PatchAsync($"/$data/{Set}({id})", JsonContent.Create(new { Status = "PLANTED" }));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);   // a "successful no-op", same precedent as a computed-only patch
            Assert.Equal("ORIGINAL", Row(app, id)?.Status);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// Reading the marked property still works — the whole point of this feature over
    /// <see cref="IyuEdmModelBuilder.Exclude{T}"/> is that the read side is untouched.
    /// </summary>
    [Fact]
    public async Task Reading_the_marked_property_still_works()
    {
        var app = await StartAsync(excludeFromWrite: true);
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TrackedOrderContext>();
                db.OrdersExt.Add(new TrackedOrderExt { Id = Guid.NewGuid(), Name = "a", Status = "VISIBLE" });
                await db.SaveChangesAsync();
            }

            using var response = await app.GetTestServer().CreateClient()
                .GetAsync($"/$data/{Set}?$select={nameof(TrackedOrderExt.Status)}");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // not the 400 Exclude<T> would give
            Assert.Contains("VISIBLE", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// A write straight to the write-side <c>DbSet</c> — standing in for a consumer's own
    /// dedicated transition endpoint — is untouched by the marker: it never goes through the
    /// generic controller's copy step at all.
    /// </summary>
    [Fact]
    public async Task A_direct_write_to_the_write_side_dbset_still_sets_the_marked_property()
    {
        var app = await StartAsync(excludeFromWrite: true);
        try
        {
            var id = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TrackedOrderContext>();
                db.Orders.Add(new TrackedOrder { Id = id, Name = "a", Status = "shipped" });
                await db.SaveChangesAsync();
            }

            Assert.Equal("shipped", Row(app, id)?.Status);
        }
        finally { await app.DisposeAsync(); }
    }
}
