using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
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

/// <summary>Write (table) side — a row version the store maintains.</summary>
public sealed class StampedOrder : IyuEntity
{
    public string Memo { get; set; } = "";
    [Timestamp] public byte[] Version { get; set; } = [];
}

/// <summary>Read (view) side — a distinct class carrying the same token under the same name.</summary>
public sealed class StampedOrderExt : IyuEntity
{
    public string Memo { get; set; } = "";
    [Timestamp] public byte[] Version { get; set; } = [];
}

public sealed class StampedOrderContext(DbContextOptions<StampedOrderContext> options) : IyuDbContext(options)
{
    public DbSet<StampedOrder> Orders => Set<StampedOrder>();
    public DbSet<StampedOrderExt> OrdersExt => Set<StampedOrderExt>();
}

public sealed class StampedOrdersController(StampedOrderContext ctx)
    : IyuODataController<StampedOrderExt, StampedOrder>(ctx);

/// <summary>
/// <c>If-Match</c> on the shape a generated app actually has: the read type and the write type are
/// different classes, and the token is a <c>[Timestamp] byte[]</c> row version rather than a
/// <c>[ConcurrencyCheck]</c> counter. The ETag is built from the read type's metadata and the
/// save-time check runs on the write type's, so a pair is the only place the two halves can be
/// seen to agree.
/// </summary>
/// <remarks>
/// InMemory neither generates a row version nor keeps a view in step with its table, so the test
/// plays the database's part: every write — the client's own included — sets a fresh version on
/// both rows, the way a <c>rowversion</c> column read through a view behaves.
/// </remarks>
public class IfMatchEntityPairEndToEndTests
{
    private const string Set = "StampedOrders";
    private static readonly Guid OrderId = Guid.NewGuid();

    private sealed record Host(WebApplication App, HttpClient Http) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }

        public async Task<T> WithDbAsync<T>(Func<StampedOrderContext, Task<T>> action)
        {
            using var scope = App.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<StampedOrderContext>());
        }
    }

    private static async Task<Host> StartAsync()
    {
        var dbName = "if-match-pair-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<StampedOrderContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(StampedOrdersController).Assembly);
                options.ODataModel.AddEntityPair<StampedOrderExt, StampedOrder>(Set);
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        var host = new Host(app, app.GetTestServer().CreateClient());
        await host.WithDbAsync(async db =>
        {
            db.Orders.Add(new StampedOrder { Id = OrderId, Memo = "first", Version = [0, 0, 0, 1] });
            db.OrdersExt.Add(new StampedOrderExt { Id = OrderId, Memo = "first", Version = [0, 0, 0, 1] });
            return await db.SaveChangesAsync();
        });
        return host;
    }

    private static async Task<string> ReadEtagAsync(Host host)
    {
        var body = await host.Http.GetStringAsync($"/$data/{Set}({OrderId})");
        return JsonDocument.Parse(body).RootElement.GetProperty("@odata.etag").GetString()!;
    }

    /// <summary>Someone else's write lands: the store moves both rows to a new version.</summary>
    private static Task<int> ChangeBehindTheClientsBackAsync(Host host) => host.WithDbAsync(async db =>
    {
        var order = await db.Orders.SingleAsync(o => o.Id == OrderId);
        var view = await db.OrdersExt.SingleAsync(o => o.Id == OrderId);
        order.Memo = view.Memo = "theirs";
        order.Version = [0, 0, 0, 2];
        view.Version = [0, 0, 0, 2];
        return await db.SaveChangesAsync();
    });

    private static Task<StampedOrder?> StoredAsync(Host host) => host.WithDbAsync(async db =>
        await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == OrderId));

    private static HttpRequestMessage Patch(object body, string? ifMatch)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/$data/{Set}({OrderId})")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return req;
    }

    [Fact]
    public async Task The_read_type_advertises_an_ETag_built_from_the_row_version()
    {
        await using var host = await StartAsync();

        var etag = await ReadEtagAsync(host);

        Assert.StartsWith("W/\"", etag);
    }

    [Fact]
    public async Task A_patch_naming_the_current_version_is_applied()
    {
        await using var host = await StartAsync();
        var etag = await ReadEtagAsync(host);

        using var res = await host.Http.SendAsync(Patch(new { Memo = "mine" }, etag));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("mine", (await StoredAsync(host))!.Memo);
    }

    [Fact]
    public async Task A_patch_naming_a_version_the_row_no_longer_has_is_refused_with_412()
    {
        await using var host = await StartAsync();
        var etag = await ReadEtagAsync(host);
        await ChangeBehindTheClientsBackAsync(host);

        using var res = await host.Http.SendAsync(Patch(new { Memo = "mine" }, etag));

        Assert.Equal(HttpStatusCode.PreconditionFailed, res.StatusCode);
        Assert.Equal("theirs", (await StoredAsync(host))!.Memo);
    }

    /// <summary>
    /// The token is the store's, never the client's. A body that carries it — echoed back from a read,
    /// or chosen — must not move the row's version, or a client could pick the version its next
    /// conditional write will be checked against. Sent alongside a real change it is dropped, the
    /// way a whole-object round trip's other read-only fields are.
    /// </summary>
    [Fact]
    public async Task A_body_carrying_the_token_with_a_real_change_stores_the_change_and_not_the_token()
    {
        await using var host = await StartAsync();

        using var res = await host.Http.SendAsync(Patch(new { Memo = "mine", Version = "AAAACQ==" }, ifMatch: null));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var stored = (await StoredAsync(host))!;
        Assert.Equal("mine", stored.Memo);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, stored.Version);
    }

    [Fact]
    public async Task A_body_carrying_only_the_token_is_refused_and_says_where_the_version_goes()
    {
        await using var host = await StartAsync();

        using var res = await host.Http.SendAsync(Patch(new { Version = "AAAACQ==" }, ifMatch: null));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var error = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(ODataErrorCodes.UnwritableProperty, error.GetProperty("code").GetString());
        Assert.Contains("If-Match", error.ToString());
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, (await StoredAsync(host))!.Version);
    }

    [Fact]
    public async Task A_create_does_not_take_the_token_from_the_body()
    {
        await using var host = await StartAsync();
        var id = Guid.NewGuid();

        using var res = await host.Http.PostAsync($"/$data/{Set}", new StringContent(
            JsonSerializer.Serialize(new { Id = id, Memo = "new", Version = "AAAACQ==" }), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var stored = await host.WithDbAsync(async db => await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id));
        Assert.Equal("new", stored.Memo);
        Assert.Empty(stored.Version);
    }
}
