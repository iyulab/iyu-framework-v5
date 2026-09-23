using System.Net;
using System.Text.Json;
using Iyu.MainServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// A single entity addressed by key answers its query options the way the collection does.
/// </summary>
/// <remarks>
/// The by-key action used to materialize the entity before <c>[EnableQuery]</c> saw it, so an
/// <c>$expand</c> ran against an object with nothing loaded: a collection navigation came back
/// <c>[]</c> and a reference as absent, with <c>200</c> — a response that looks valid and is wrong.
/// Each case below is asserted against rows that exist, and paired with the collection route
/// answering the same question, so "empty" can only mean the key route lost them.
/// </remarks>
public class KeyedExpandEndToEndTests
{
    private const string Units = "SizeUnits";
    private const string Readings = "SizeReadings";

    private sealed record Seeded(WebApplication App, Guid UnitId, Guid ReadingId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Seeded> StartAsync()
    {
        var dbName = "keyed-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<SizeContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(SizeUnitsController).Assembly);
                options.ODataModel.AddEntityPair<SizeReadingExt, SizeReading>(Readings);
                options.ODataModel.AddEntityPair<SizeUnitExt, SizeUnit>(Units);
            });

        var app = builder.Build();
        app.UseIyuMainServer();

        var unit = new SizeUnitExt { Id = Guid.NewGuid(), Seq = 1 };
        var reading = new SizeReadingExt { Id = Guid.NewGuid(), Seq = 10 };
        unit.Readings.Add(reading);
        unit.Readings.Add(new SizeReadingExt { Id = Guid.NewGuid(), Seq = 11 });
        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SizeContext>();
            ctx.UnitsExt.Add(unit);
            await ctx.SaveChangesAsync();
        }

        await app.StartAsync();
        return new Seeded(app, unit.Id, reading.Id);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length == 0) return (response.StatusCode, default);
        using var document = JsonDocument.Parse(text);
        return (response.StatusCode, document.RootElement.Clone());
    }

    [Fact]
    public async Task A_collection_navigation_is_expanded_on_the_key_route()
    {
        await using var seeded = await StartAsync();
        using var client = seeded.App.GetTestClient();

        var (status, entity) = await GetAsync(client, $"/$data/{Units}({seeded.UnitId})?$expand=Readings");
        var (_, list) = await GetAsync(client, $"/$data/{Units}?$filter=Id eq {seeded.UnitId}&$expand=Readings");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, Assert.Single(list.GetProperty("value").EnumerateArray()).GetProperty("Readings").GetArrayLength());
        Assert.Equal(2, entity.GetProperty("Readings").GetArrayLength());
    }

    [Fact]
    public async Task A_reference_navigation_is_expanded_on_the_key_route()
    {
        await using var seeded = await StartAsync();
        using var client = seeded.App.GetTestClient();

        var (status, entity) = await GetAsync(client, $"/$data/{Readings}({seeded.ReadingId})?$expand=Unit");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(seeded.UnitId, entity.GetProperty("Unit").GetProperty("Id").GetGuid());
    }

    [Fact]
    public async Task Select_applies_on_the_key_route()
    {
        await using var seeded = await StartAsync();
        using var client = seeded.App.GetTestClient();

        var (status, entity) = await GetAsync(client, $"/$data/{Units}({seeded.UnitId})?$select=Seq");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, entity.GetProperty("Seq").GetInt32());
        Assert.False(entity.TryGetProperty("CreatedAt", out _));
    }

    [Fact]
    public async Task An_unknown_key_is_404()
    {
        await using var seeded = await StartAsync();
        using var client = seeded.App.GetTestClient();

        var (status, _) = await GetAsync(client, $"/$data/{Units}({Guid.NewGuid()})");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task An_unknown_key_with_an_expand_is_still_404()
    {
        await using var seeded = await StartAsync();
        using var client = seeded.App.GetTestClient();

        var (status, _) = await GetAsync(client, $"/$data/{Units}({Guid.NewGuid()})?$expand=Readings");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
