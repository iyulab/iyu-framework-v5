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

public sealed class VersionedNote : IyuEntity
{
    public string Text { get; set; } = "";
    [ConcurrencyCheck] public int Revision { get; set; }
}

public sealed class PlainNote : IyuEntity
{
    public string Text { get; set; } = "";
}

public sealed class VersionedNoteContext(DbContextOptions<VersionedNoteContext> options) : IyuDbContext(options)
{
    public DbSet<VersionedNote> Versioned => Set<VersionedNote>();
    public DbSet<PlainNote> Plain => Set<PlainNote>();
}

public sealed class VersionedNotesController(VersionedNoteContext ctx)
    : IyuODataController<VersionedNote, VersionedNote>(ctx);

public sealed class PlainNotesController(VersionedNoteContext ctx)
    : IyuODataController<PlainNote, PlainNote>(ctx);

/// <summary>
/// A set whose type has a concurrency property is answered with <c>@odata.etag</c>; a write that sends
/// <c>If-Match</c> must then be refused with 412 when the row has moved on, instead of overwriting it.
/// </summary>
public class IfMatchEndToEndTests
{
    private sealed record Host(WebApplication App, HttpClient Http, string DbName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }

        public async Task<T> WithDbAsync<T>(Func<VersionedNoteContext, Task<T>> action)
        {
            using var scope = App.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<VersionedNoteContext>());
        }
    }

    private static readonly Guid NoteId = Guid.NewGuid();

    private static async Task<Host> StartAsync()
    {
        var dbName = "if-match-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<VersionedNoteContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(VersionedNotesController).Assembly);
                options.ODataModel.AddEntityPair<VersionedNote, VersionedNote>("VersionedNotes");
                options.ODataModel.AddEntityPair<PlainNote, PlainNote>("PlainNotes");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        var host = new Host(app, app.GetTestServer().CreateClient(), dbName);
        await host.WithDbAsync(async db =>
        {
            db.Versioned.Add(new VersionedNote { Id = NoteId, Text = "first", Revision = 1 });
            db.Plain.Add(new PlainNote { Id = NoteId, Text = "first" });
            return await db.SaveChangesAsync();
        });
        return host;
    }

    private static async Task<string> ReadEtagAsync(Host host)
    {
        var body = await host.Http.GetStringAsync($"/$data/VersionedNotes({NoteId})");
        return JsonDocument.Parse(body).RootElement.GetProperty("@odata.etag").GetString()!;
    }

    /// <summary>Someone else's write lands: the row is no longer the version a client read.</summary>
    private static Task<int> ChangeBehindTheClientsBackAsync(Host host) => host.WithDbAsync(async db =>
    {
        var note = await db.Versioned.SingleAsync(n => n.Id == NoteId);
        note.Text = "theirs";
        note.Revision = 2;
        return await db.SaveChangesAsync();
    });

    private static Task<string?> StoredTextAsync(Host host) => host.WithDbAsync(async db =>
        (await db.Versioned.AsNoTracking().SingleOrDefaultAsync(n => n.Id == NoteId))?.Text);

    private static HttpRequestMessage Patch(string set, string text, string? ifMatch)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/$data/{set}({NoteId})")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { Text = text }), Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return req;
    }

    private static HttpRequestMessage Delete(string? ifMatch)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/$data/VersionedNotes({NoteId})");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return req;
    }

    [Fact]
    public async Task A_patch_naming_the_current_version_is_applied()
    {
        await using var host = await StartAsync();
        var etag = await ReadEtagAsync(host);

        using var res = await host.Http.SendAsync(Patch("VersionedNotes", "mine", etag));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("mine", await StoredTextAsync(host));
    }

    [Fact]
    public async Task A_patch_naming_a_version_the_row_no_longer_has_is_refused_with_412()
    {
        await using var host = await StartAsync();
        var etag = await ReadEtagAsync(host);
        await ChangeBehindTheClientsBackAsync(host);

        using var res = await host.Http.SendAsync(Patch("VersionedNotes", "mine", etag));

        Assert.Equal(HttpStatusCode.PreconditionFailed, res.StatusCode);
        var error = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(ODataErrorCodes.PreconditionFailed, error.GetProperty("code").GetString());
        Assert.Equal("theirs", await StoredTextAsync(host));
    }

    [Fact]
    public async Task A_delete_naming_a_stale_version_is_refused_and_the_current_one_is_honoured()
    {
        await using var host = await StartAsync();
        var stale = await ReadEtagAsync(host);
        await ChangeBehindTheClientsBackAsync(host);

        using (var refused = await host.Http.SendAsync(Delete(stale)))
            Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        Assert.Equal("theirs", await StoredTextAsync(host));

        using (var deleted = await host.Http.SendAsync(Delete(await ReadEtagAsync(host))))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await StoredTextAsync(host));
    }

    [Theory]
    [InlineData("*")]
    [InlineData(null)]
    public async Task A_wildcard_or_no_If_Match_leaves_the_write_unconditional(string? ifMatch)
    {
        await using var host = await StartAsync();
        await ChangeBehindTheClientsBackAsync(host);

        using var res = await host.Http.SendAsync(Patch("VersionedNotes", "mine", ifMatch));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("mine", await StoredTextAsync(host));
    }

    /// <summary>A set without concurrency properties never advertises an ETag, and keeps ignoring one.</summary>
    [Fact]
    public async Task A_set_without_concurrency_properties_is_unchanged()
    {
        await using var host = await StartAsync();

        using var res = await host.Http.SendAsync(Patch("PlainNotes", "mine", "W/\"bogus\""));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    /// <summary>
    /// A concurrent write that lands after the If-Match check and before the save. The check cannot
    /// see it; the save's own concurrency check does, and for a conditional request that failure is
    /// the same 412 — not the 409 an unconditional write's concurrency conflict gets.
    /// </summary>
    [Fact]
    public async Task A_change_landing_between_the_check_and_the_save_is_refused_with_412()
    {
        var dbName = "if-match-race-" + Guid.NewGuid().ToString("N");
        var race = new ConcurrentWriteOnce(dbName);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<VersionedNoteContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName).AddInterceptors(race),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(VersionedNotesController).Assembly);
                options.ODataModel.AddEntityPair<VersionedNote, VersionedNote>("VersionedNotes");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        await using var host = new Host(app, app.GetTestServer().CreateClient(), dbName);
        await host.WithDbAsync(async db =>
        {
            db.Versioned.Add(new VersionedNote { Id = NoteId, Text = "first", Revision = 1 });
            return await db.SaveChangesAsync();
        });
        var etag = await ReadEtagAsync(host);
        race.Armed = true;

        using var res = await host.Http.SendAsync(Patch("VersionedNotes", "mine", etag));

        Assert.True(race.Fired);
        Assert.Equal(HttpStatusCode.PreconditionFailed, res.StatusCode);
        Assert.Equal("theirs", await StoredTextAsync(host));
    }

    /// <summary>Writes "theirs" through a separate context the first time an armed save starts.</summary>
    private sealed class ConcurrentWriteOnce(string dbName) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !Fired)
            {
                Fired = true;
                var options = new DbContextOptionsBuilder<VersionedNoteContext>().UseInMemoryDatabase(dbName).Options;
                await using var other = new VersionedNoteContext(options);
                var note = await other.Versioned.SingleAsync(n => n.Id == NoteId, cancellationToken);
                note.Text = "theirs";
                note.Revision = 2;
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task A_single_entity_read_carries_the_ETag_header_too()
    {
        await using var host = await StartAsync();

        using var res = await host.Http.GetAsync($"/$data/VersionedNotes({NoteId})");

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(body.GetProperty("@odata.etag").GetString(), res.Headers.ETag?.ToString());
    }
}
