using System.Net;
using System.Net.Http.Json;
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

public sealed class TaggedItem : IyuEntity
{
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<int>? Scores { get; set; }
}

public sealed class TaggedItemExt : IyuEntity
{
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<int>? Scores { get; set; }
}

public sealed class TaggedContext(DbContextOptions<TaggedContext> options) : IyuDbContext(options)
{
    public DbSet<TaggedItem> Items => Set<TaggedItem>();
    public DbSet<TaggedItemExt> ItemExts => Set<TaggedItemExt>();
}

public sealed class TaggedItemsController(TaggedContext ctx) : IyuODataController<TaggedItemExt, TaggedItem>(ctx);

/// <summary>
/// A property that holds a list of primitive values — what EF Core maps as a primitive collection and the EDM
/// describes as <c>Collection(Edm.String)</c> — round-trips through <c>/$data</c>: written by POST and PATCH,
/// read back whole, filtered with <c>any()</c>.
/// </summary>
public class PrimitiveCollectionEndToEndTests
{
    private sealed record Host(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        public HttpClient Client() => App.GetTestServer().CreateClient();
        public async ValueTask DisposeAsync() { await App.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    // SQLite, not the in-memory provider: a primitive collection is stored as JSON text and `any()` is translated
    // to a query over it — the relational path a real database takes.
    private static async Task<Host> StartAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<TaggedContext>(
            configureDb: db => db.UseSqlite(connection),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(TaggedItemsController).Assembly);
                options.ODataModel.AddEntityPair<TaggedItemExt, TaggedItem>("TaggedItems");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaggedContext>();
        await db.Database.EnsureCreatedAsync();
        return new Host(app, connection);
    }

    // The write type and the read type are separate tables here (no view), so a row written through the API is
    // mirrored into the read table the way a database view would show it.
    private static async Task MirrorAsync(Host host)
    {
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaggedContext>();
        db.ItemExts.RemoveRange(db.ItemExts);
        foreach (var item in await db.Items.AsNoTracking().ToListAsync())
        {
            db.ItemExts.Add(new TaggedItemExt { Id = item.Id, Name = item.Name, Tags = item.Tags, Scores = item.Scores });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<string> BodyOf(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");
        return body;
    }

    private static async Task<Guid> PostAsync(Host host, object body)
    {
        using var response = await host.Client().PostAsJsonAsync("/$data/TaggedItems", body);
        using var doc = JsonDocument.Parse(await BodyOf(response, HttpStatusCode.Created));
        // The created entity is answered in the web serializer's casing, not the OData payload's.
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<List<string>> TagsInWriteTableAsync(Host host, Guid id)
    {
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaggedContext>();
        return (await db.Items.AsNoTracking().SingleAsync(i => i.Id == id)).Tags;
    }

    [Fact]
    public async Task The_metadata_describes_the_list_as_a_collection_of_its_element_type()
    {
        await using var host = await StartAsync();

        var body = await BodyOf(await host.Client().GetAsync("/$data/$metadata"), HttpStatusCode.OK);

        Assert.Contains("Name=\"Tags\" Type=\"Collection(Edm.String)\"", body);
        Assert.Contains("Name=\"Scores\" Type=\"Collection(Edm.Int32)\"", body);
    }

    [Fact]
    public async Task A_post_stores_the_list_and_a_read_returns_it_whole()
    {
        await using var host = await StartAsync();

        var id = await PostAsync(host, new { Name = "one", Tags = new[] { "red", "blue" }, Scores = new[] { 3, 1 } });
        Assert.Equal(["red", "blue"], await TagsInWriteTableAsync(host, id));

        await MirrorAsync(host);
        var body = await BodyOf(await host.Client().GetAsync($"/$data/TaggedItems({id})"), HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["red", "blue"], doc.RootElement.GetProperty("Tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal([3, 1], doc.RootElement.GetProperty("Scores").EnumerateArray().Select(s => s.GetInt32()));
    }

    [Fact]
    public async Task A_patch_replaces_the_list()
    {
        await using var host = await StartAsync();
        var id = await PostAsync(host, new { Name = "one", Tags = new[] { "red", "blue" } });
        await MirrorAsync(host);

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/$data/TaggedItems({id})")
        {
            Content = JsonContent.Create(new { Tags = new[] { "green" } }),
        };
        await BodyOf(await host.Client().SendAsync(patch), HttpStatusCode.NoContent);

        Assert.Equal(["green"], await TagsInWriteTableAsync(host, id));
    }

    [Fact]
    public async Task Any_filters_on_an_element_of_the_list()
    {
        await using var host = await StartAsync();
        await PostAsync(host, new { Name = "warm", Tags = new[] { "red", "orange" } });
        await PostAsync(host, new { Name = "cold", Tags = new[] { "blue" } });
        await PostAsync(host, new { Name = "none", Tags = Array.Empty<string>() });
        await MirrorAsync(host);

        var filter = Uri.EscapeDataString("Tags/any(t: t eq 'red')");
        var body = await BodyOf(
            await host.Client().GetAsync($"/$data/TaggedItems?$filter={filter}&$select=Name"), HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["warm"], doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("Name").GetString()));
    }
}
