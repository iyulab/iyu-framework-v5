using System.Net;
using System.Text.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the reason ODataTestServerRoutingTests documents.

public sealed class OptionalRefOwner : IyuEntity { public string Name { get; set; } = ""; }
public sealed class OptionalRefOwnerExt : IyuEntity { public string Name { get; set; } = ""; public string? Note { get; set; } }

public sealed class OptionalRefItem : IyuEntity { public Guid? OwnerId { get; set; } }

public sealed class OptionalRefItemExt : IyuEntity
{
    public Guid? OwnerId { get; set; }
    public OptionalRefOwnerExt? Owner { get; set; }
}

public sealed class OptionalRefContext(DbContextOptions<OptionalRefContext> options) : IyuDbContext(options)
{
    public DbSet<OptionalRefOwner> Owners => Set<OptionalRefOwner>();
    public DbSet<OptionalRefOwnerExt> OwnerExts => Set<OptionalRefOwnerExt>();
    public DbSet<OptionalRefItem> Items => Set<OptionalRefItem>();
    public DbSet<OptionalRefItemExt> ItemExts => Set<OptionalRefItemExt>();
}

public sealed class OptionalRefItemsController(OptionalRefContext ctx) : IyuODataController<OptionalRefItemExt, OptionalRefItem>(ctx);

/// <summary>
/// A <c>groupby</c> over a path through an optional reference, where some rows have none: the rows without one
/// form a group of their own, and the answer is written whole.
/// </summary>
/// <remarks>
/// The rows without a reference were grouped under a reference whose every grouped value is null — its key
/// included, which the model declares non-nullable — and the writer stopped in the middle of a body whose 200 had
/// already gone out.
/// </remarks>
public class ApplyOverOptionalReferenceEndToEndTests
{
    private static readonly Guid Ann = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed record Host(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await App.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    // SQLite, not the in-memory provider: what an absent reference reads as is the provider's — a relational one
    // joins it as null, the in-memory one dereferences it and throws before anything is written.
    private static async Task<Host> StartAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<OptionalRefContext>(
            configureDb: db => db.UseSqlite(connection),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(OptionalRefItemsController).Assembly);
                options.ODataModel.AddEntityPair<OptionalRefItemExt, OptionalRefItem>("OptionalRefItems");
                options.ODataModel.AddEntityPair<OptionalRefOwnerExt, OptionalRefOwner>("OptionalRefOwners");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptionalRefContext>();
        await db.Database.EnsureCreatedAsync();
        db.OwnerExts.AddRange(
            new OptionalRefOwnerExt { Id = Ann, Name = "ann", Note = "n" },
            new OptionalRefOwnerExt { Id = Bob, Name = "bob", Note = null });
        db.ItemExts.AddRange(
            new OptionalRefItemExt { Id = Guid.NewGuid(), OwnerId = Ann },
            new OptionalRefItemExt { Id = Guid.NewGuid(), OwnerId = Ann },
            new OptionalRefItemExt { Id = Guid.NewGuid(), OwnerId = Bob },
            new OptionalRefItemExt { Id = Guid.NewGuid(), OwnerId = null },
            new OptionalRefItemExt { Id = Guid.NewGuid(), OwnerId = null },
            new OptionalRefItemExt { Id = Guid.NewGuid(), OwnerId = null });
        await db.SaveChangesAsync();
        return new Host(app, connection);
    }

    private static async Task<JsonElement[]> GroupsAsync(Host host, string apply)
    {
        var client = host.App.GetTestServer().CreateClient();
        using var response = await client.GetAsync("/$data/OptionalRefItems?$apply=" + Uri.EscapeDataString(apply));
        // Read the body to its end: the failure was a 200 whose body broke off part-way.
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static int Count(JsonElement group) => group.GetProperty("C").GetInt32();

    [Fact]
    public async Task Grouping_by_the_key_of_an_optional_reference_puts_the_rows_without_one_in_a_null_group()
    {
        await using var host = await StartAsync();

        var groups = await GroupsAsync(host, "groupby((Owner/Id),aggregate($count as C))");

        Assert.Equal(3, groups.Length);
        var none = Assert.Single(groups, g => g.GetProperty("Owner").ValueKind == JsonValueKind.Null);
        Assert.Equal(3, Count(none));
        var named = groups.Where(g => g.GetProperty("Owner").ValueKind != JsonValueKind.Null)
            .ToDictionary(g => g.GetProperty("Owner").GetProperty("Id").GetGuid(), Count);
        Assert.Equal(new Dictionary<Guid, int> { [Ann] = 2, [Bob] = 1 }, named);
    }

    [Fact]
    public async Task Grouping_without_an_aggregate_is_written_whole_too()
    {
        await using var host = await StartAsync();

        var groups = await GroupsAsync(host, "groupby((Owner/Id))");

        Assert.Equal(3, groups.Length);
        Assert.Single(groups, g => g.GetProperty("Owner").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_nullable_property_of_a_present_reference_is_still_a_group_of_its_own()
    {
        // Bob is there and has no note: that group is Bob's reference with a null note — not «no reference».
        await using var host = await StartAsync();

        var groups = await GroupsAsync(host, "groupby((Owner/Name,Owner/Note),aggregate($count as C))");

        Assert.Equal(3, groups.Length);
        Assert.Equal(3, Count(Assert.Single(groups, g => g.GetProperty("Owner").ValueKind == JsonValueKind.Null)));
        var bob = Assert.Single(groups, g => g.GetProperty("Owner") is { ValueKind: JsonValueKind.Object } o
                                             && o.GetProperty("Name").GetString() == "bob");
        Assert.Equal(JsonValueKind.Null, bob.GetProperty("Owner").GetProperty("Note").ValueKind);
        Assert.Equal(1, Count(bob));
    }
}
