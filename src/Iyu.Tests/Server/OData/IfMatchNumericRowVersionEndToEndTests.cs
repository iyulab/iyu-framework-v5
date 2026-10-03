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

/// <summary>Write (table) side — a numeric row version, the shape PostgreSQL's <c>xmin</c> takes in EF.</summary>
public sealed class NumberedTicket : IyuEntity
{
    public string Memo { get; set; } = "";
    [Timestamp] public uint Version { get; set; }
}

/// <summary>Read (view) side — the same token under the same name.</summary>
public sealed class NumberedTicketExt : IyuEntity
{
    public string Memo { get; set; } = "";
    [Timestamp] public uint Version { get; set; }
}

public sealed class NumberedTicketContext(DbContextOptions<NumberedTicketContext> options) : IyuDbContext(options)
{
    public DbSet<NumberedTicket> Tickets => Set<NumberedTicket>();
    public DbSet<NumberedTicketExt> TicketsExt => Set<NumberedTicketExt>();
}

public sealed class NumberedTicketsController(NumberedTicketContext ctx)
    : IyuODataController<NumberedTicketExt, NumberedTicket>(ctx);

/// <summary>
/// <c>If-Match</c> on a read/write pair whose row version is a <c>uint</c> — how EF maps
/// PostgreSQL's <c>xmin</c> system column — rather than SQL Server's <c>byte[]</c>
/// (<see cref="IfMatchEntityPairEndToEndTests"/>). OData has no unsigned 32-bit type, so the
/// version reaches the wire as a wider integer; the ETag and the save-time check must still agree.
/// </summary>
/// <remarks>
/// As in the <c>byte[]</c> tests, InMemory does not maintain a row version, so the test moves both
/// rows to a new version itself whenever "someone else" writes.
/// </remarks>
public class IfMatchNumericRowVersionEndToEndTests
{
    private const string Set = "NumberedTickets";
    private static readonly Guid TicketId = Guid.NewGuid();

    private sealed record Host(WebApplication App, HttpClient Http) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }

        public async Task<T> WithDbAsync<T>(Func<NumberedTicketContext, Task<T>> action)
        {
            using var scope = App.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<NumberedTicketContext>());
        }
    }

    private static async Task<Host> StartAsync()
    {
        var dbName = "if-match-numeric-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<NumberedTicketContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(NumberedTicketsController).Assembly);
                options.ODataModel.AddEntityPair<NumberedTicketExt, NumberedTicket>(Set);
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        var host = new Host(app, app.GetTestServer().CreateClient());
        await host.WithDbAsync(async db =>
        {
            db.Tickets.Add(new NumberedTicket { Id = TicketId, Memo = "first", Version = 7 });
            db.TicketsExt.Add(new NumberedTicketExt { Id = TicketId, Memo = "first", Version = 7 });
            return await db.SaveChangesAsync();
        });
        return host;
    }

    private static async Task<string> ReadEtagAsync(Host host)
    {
        var body = await host.Http.GetStringAsync($"/$data/{Set}({TicketId})");
        return JsonDocument.Parse(body).RootElement.GetProperty("@odata.etag").GetString()!;
    }

    private static Task<int> ChangeBehindTheClientsBackAsync(Host host) => host.WithDbAsync(async db =>
    {
        var ticket = await db.Tickets.SingleAsync(t => t.Id == TicketId);
        var view = await db.TicketsExt.SingleAsync(t => t.Id == TicketId);
        ticket.Memo = view.Memo = "theirs";
        ticket.Version = view.Version = 8;
        return await db.SaveChangesAsync();
    });

    private static Task<NumberedTicket?> StoredAsync(Host host) => host.WithDbAsync(async db =>
        await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == TicketId));

    private static HttpRequestMessage Patch(object body, string? ifMatch)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/$data/{Set}({TicketId})")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return req;
    }

    [Fact]
    public async Task The_read_type_advertises_an_ETag_and_the_version_as_a_number()
    {
        await using var host = await StartAsync();

        var body = JsonDocument.Parse(await host.Http.GetStringAsync($"/$data/{Set}({TicketId})")).RootElement;

        Assert.StartsWith("W/\"", body.GetProperty("@odata.etag").GetString());
        Assert.Equal(7, body.GetProperty("Version").GetInt64());
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

    [Fact]
    public async Task A_body_carrying_only_the_token_is_refused()
    {
        await using var host = await StartAsync();

        using var res = await host.Http.SendAsync(Patch(new { Version = 99 }, ifMatch: null));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var error = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(ODataErrorCodes.UnwritableProperty, error.GetProperty("code").GetString());
        Assert.Equal(7u, (await StoredAsync(host))!.Version);
    }
}
