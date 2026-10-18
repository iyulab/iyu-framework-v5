using System.Net;
using System.Text;
using System.Text.Json;
using Iyu.Core.Attributes;
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

public sealed class MeasuredItem : IyuEntity
{
    public List<string> Codes { get; set; } = [];
    public List<decimal> Rates { get; set; } = [];
}

public sealed class MeasuredItemExt : IyuEntity
{
    [ElementStringLength(4)]
    public List<string> Codes { get; set; } = [];
    [ElementDecimalDigits(5, 2)]
    public List<decimal> Rates { get; set; } = [];
}

public sealed class MeasuredContext(DbContextOptions<MeasuredContext> options) : IyuDbContext(options)
{
    public DbSet<MeasuredItem> Items => Set<MeasuredItem>();
    public DbSet<MeasuredItemExt> ItemExts => Set<MeasuredItemExt>();
}

public sealed class MeasuredItemsController(MeasuredContext ctx) : IyuODataController<MeasuredItemExt, MeasuredItem>(ctx);

/// <summary>
/// A list property's element rule is enforced at the write boundary like a single value's: an element beyond its
/// length or digits is a <c>400 InvalidBody</c> naming the property — not a list stored whole for the database to
/// truncate, round, or (as JSON text) keep as sent.
/// </summary>
public class ElementValidationEndToEndTests
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
        builder.Services.AddIyuMainServer<MeasuredContext>(
            configureDb: db => db.UseSqlite(connection),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(MeasuredItemsController).Assembly);
                options.ODataModel.AddEntityPair<MeasuredItemExt, MeasuredItem>("MeasuredItems");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeasuredContext>();
        await db.Database.EnsureCreatedAsync();
        return new Host(app, connection);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(string Code, string? Target, string? DetailCode)> RefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        var detail = Assert.Single(error.GetProperty("details").EnumerateArray());
        return (error.GetProperty("code").GetString()!,
            detail.TryGetProperty("target", out var t) ? t.GetString() : null,
            detail.TryGetProperty("code", out var c) ? c.GetString() : null);
    }

    [Theory]
    [InlineData("""{"Codes":["ab","abcde"],"Rates":[1.5]}""", "Codes")]
    [InlineData("""{"Codes":["ab"],"Rates":[1.5,1.234]}""", "Rates")]
    [InlineData("""{"Codes":["ab"],"Rates":[1000.5]}""", "Rates")]
    public async Task An_element_beyond_its_rule_is_refused_naming_the_list(string body, string target)
    {
        await using var host = await StartAsync();

        var (code, refusedTarget, detailCode) = await RefusalAsync(await host.Client().PostAsync("/$data/MeasuredItems", Json(body)));

        Assert.Equal(ODataErrorCodes.InvalidBody, code);
        Assert.Equal(target, refusedTarget);
        Assert.Equal(ODataErrorDetailCodes.ValidationFailed, detailCode);
    }

    [Fact]
    public async Task Elements_within_their_rules_are_created()
    {
        await using var host = await StartAsync();

        using var created = await host.Client().PostAsync("/$data/MeasuredItems", Json("""{"Codes":["ab","abcd"],"Rates":[1.5,999.99]}"""));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
