using System.Net;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Data.WriteRules;
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

public sealed class ErasableNote : IyuEntity
{
    public string Text { get; set; } = "";
}

public sealed class ErasedNoteRecord : IyuEntity
{
    public Guid NoteId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class ErasableNoteContext(DbContextOptions<ErasableNoteContext> options) : IyuDbContext(options)
{
    public DbSet<ErasableNote> Notes => Set<ErasableNote>();
    public DbSet<ErasedNoteRecord> Erased => Set<ErasedNoteRecord>();
}

public sealed class ErasableNotesController(ErasableNoteContext ctx)
    : IyuODataController<ErasableNote, ErasableNote>(ctx);

/// <summary>Follows a deleted note with a record of it, written through the rule's own context.</summary>
public sealed class RecordNoteErasure : EntityDeleteRule<ErasableNote>
{
    protected override void OnDeleting(WriteRuleContext<ErasableNote> ctx)
        => ctx.Db.Add(new ErasedNoteRecord { NoteId = ctx.Entity.Id, Text = ctx.Entity.Text });
}

public sealed class RefuseLockedNoteErasure : EntityDeleteRule<ErasableNote>
{
    protected override void OnDeleting(WriteRuleContext<ErasableNote> ctx)
    {
        if (ctx.Entity.Text == "locked")
            throw new WriteRuleViolationException("NoteLocked", "A locked note cannot be deleted.");
    }
}

/// <summary>
/// The reason a delete rule exists is that a delete arrives through more than one entry point and the
/// derived record has to follow all of them. This is the entry point most apps open first: the generic
/// OData <c>DELETE</c>, through a host wired by <c>AddIyuMainServer</c>.
/// </summary>
public class DeleteRuleEndToEndTests
{
    private static readonly Guid NoteId = Guid.NewGuid();

    private sealed record Host(WebApplication App, HttpClient Http) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }

        public async Task<T> WithDbAsync<T>(Func<ErasableNoteContext, Task<T>> action)
        {
            using var scope = App.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<ErasableNoteContext>());
        }
    }

    private static async Task<Host> StartAsync(string text)
    {
        var dbName = "delete-rule-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<IEntityWriteRule, RecordNoteErasure>();
        builder.Services.AddScoped<IEntityWriteRule, RefuseLockedNoteErasure>();
        builder.Services.AddIyuMainServer<ErasableNoteContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ErasableNotesController).Assembly);
                options.ODataModel.AddEntityPair<ErasableNote, ErasableNote>("ErasableNotes");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        var host = new Host(app, app.GetTestServer().CreateClient());
        await host.WithDbAsync(async db =>
        {
            db.Notes.Add(new ErasableNote { Id = NoteId, Text = text });
            return await db.SaveChangesAsync();
        });
        return host;
    }

    [Fact]
    public async Task An_odata_delete_runs_the_delete_rule_and_its_record_is_saved_with_the_delete()
    {
        await using var host = await StartAsync("remember me");

        using var res = await host.Http.DeleteAsync($"/$data/ErasableNotes({NoteId})");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var remaining = await host.WithDbAsync(db => db.Notes.CountAsync());
        var record = await host.WithDbAsync(db => db.Erased.SingleAsync());
        Assert.Equal(0, remaining);
        Assert.Equal(NoteId, record.NoteId);
        Assert.Equal("remember me", record.Text);
    }

    [Fact]
    public async Task A_delete_rule_that_refuses_is_answered_422_with_its_code_and_the_row_stays()
    {
        await using var host = await StartAsync("locked");

        using var res = await host.Http.DeleteAsync($"/$data/ErasableNotes({NoteId})");

        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Contains("NoteLocked", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, await host.WithDbAsync(db => db.Notes.CountAsync()));
        Assert.Equal(0, await host.WithDbAsync(db => db.Erased.CountAsync()));
    }
}
