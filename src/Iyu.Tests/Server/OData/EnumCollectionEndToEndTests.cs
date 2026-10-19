using System.Net;
using System.Net.Http.Json;
using System.Runtime.Serialization;
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

public enum Shade
{
    [EnumMember(Value = "light_red")] LightRed,
    [EnumMember(Value = "deep_blue")] DeepBlue,
    [EnumMember(Value = "grey")] Grey,
}

public sealed class ShadedItem : IyuEntity
{
    public string Name { get; set; } = "";
    public List<Shade> Shades { get; set; } = [];
    public List<Shade>? Accents { get; set; }
}

public sealed class ShadedItemExt : IyuEntity
{
    public string Name { get; set; } = "";
    public List<Shade> Shades { get; set; } = [];
    public List<Shade>? Accents { get; set; }
}

public sealed class ShadedContext(DbContextOptions<ShadedContext> options) : IyuDbContext(options)
{
    public DbSet<ShadedItem> Items => Set<ShadedItem>();
    public DbSet<ShadedItemExt> ItemExts => Set<ShadedItemExt>();
}

public sealed class ShadedItemsController(ShadedContext ctx) : IyuODataController<ShadedItemExt, ShadedItem>(ctx);

/// <summary>
/// A list of enum values — a primitive collection whose element is an enum with <see cref="EnumMemberAttribute"/>
/// wire names — is stored, described, written and filtered by those wire names, the way a single enum property is:
/// the database holds <c>light_red</c>, not <c>LightRed</c> or <c>0</c>, and <c>$metadata</c> names the members so.
/// </summary>
public class EnumCollectionEndToEndTests
{
    private sealed record Host(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        public HttpClient Client() => App.GetTestServer().CreateClient();
        public async ValueTask DisposeAsync() { await App.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private static async Task<Host> StartAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<ShadedContext>(
            configureDb: db => db.UseSqlite(connection),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ShadedItemsController).Assembly);
                options.ODataModel.AddEntityPair<ShadedItemExt, ShadedItem>("ShadedItems");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ShadedContext>().Database.EnsureCreatedAsync();
        return new Host(app, connection);
    }

    private static async Task MirrorAsync(Host host)
    {
        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShadedContext>();
        db.ItemExts.RemoveRange(db.ItemExts);
        foreach (var item in await db.Items.AsNoTracking().ToListAsync())
            db.ItemExts.Add(new ShadedItemExt { Id = item.Id, Name = item.Name, Shades = item.Shades, Accents = item.Accents });
        await db.SaveChangesAsync();
    }

    private static async Task<string> BodyOf(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");
        return body;
    }

    private static async Task<Guid> PostAsync(Host host, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await host.Client().PostAsync("/$data/ShadedItems", content);
        using var doc = JsonDocument.Parse(await BodyOf(response, HttpStatusCode.Created));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static string StoredShades(Host host)
    {
        using var command = host.Connection.CreateCommand();
        command.CommandText = "SELECT \"Shades\" FROM \"Items\"";
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task The_metadata_describes_a_collection_of_the_enum_with_its_wire_names()
    {
        await using var host = await StartAsync();

        var body = await BodyOf(await host.Client().GetAsync("/$data/$metadata"), HttpStatusCode.OK);

        Assert.Matches("Name=\"Shades\" Type=\"Collection\\([A-Za-z.]+\\.Shade\\)\"", body);
        Assert.Contains("Member Name=\"light_red\"", body);
        Assert.DoesNotContain("Member Name=\"LightRed\"", body);
    }

    [Fact]
    public async Task A_post_stores_wire_names_and_a_read_returns_them()
    {
        await using var host = await StartAsync();

        var id = await PostAsync(host, """{"Name":"one","Shades":["light_red","deep_blue"]}""");

        Assert.Equal("""["light_red","deep_blue"]""", StoredShades(host));
        await MirrorAsync(host);
        var body = await BodyOf(await host.Client().GetAsync($"/$data/ShadedItems({id})"), HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["light_red", "deep_blue"], doc.RootElement.GetProperty("Shades").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task Any_filters_on_an_element_by_its_wire_name()
    {
        await using var host = await StartAsync();
        await PostAsync(host, """{"Name":"warm","Shades":["light_red","grey"]}""");
        await PostAsync(host, """{"Name":"cold","Shades":["deep_blue"]}""");
        await MirrorAsync(host);

        var filter = Uri.EscapeDataString("Shades/any(s: s eq 'light_red')");
        var body = await BodyOf(
            await host.Client().GetAsync($"/$data/ShadedItems?$filter={filter}&$select=Name"), HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["warm"], doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("Name").GetString()));
    }

    [Fact]
    public async Task A_value_that_is_not_a_member_is_refused()
    {
        await using var host = await StartAsync();

        using var content = new StringContent("""{"Name":"x","Shades":["purple"]}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await host.Client().PostAsync("/$data/ShadedItems", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_absent_nullable_list_reads_back_absent()
    {
        await using var host = await StartAsync();
        var id = await PostAsync(host, """{"Name":"plain","Shades":[]}""");

        using var scope = host.App.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<ShadedContext>().Items.AsNoTracking().SingleAsync(i => i.Id == id);
        Assert.Null(row.Accents);
        Assert.Empty(row.Shades);
    }
}
