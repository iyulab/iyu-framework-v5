using System.Net;
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

// Top-level public types for the reason ODataTestServerRoutingTests documents: a nested controller
// is not IsPublic and MVC's ControllerFeatureProvider skips it.

public sealed class SizeReading : IyuEntity { public int Seq { get; set; } public Guid? UnitId { get; set; } }
public sealed class SizeReadingExt : IyuEntity
{
    public int Seq { get; set; }
    public Guid? UnitId { get; set; }
    public SizeUnitExt? Unit { get; set; }
}
public sealed class SizeUnit : IyuEntity { public int Seq { get; set; } }
public sealed class SizeUnitExt : IyuEntity
{
    public int Seq { get; set; }
    public List<SizeReadingExt> Readings { get; set; } = [];
}

public sealed class SizeContext(DbContextOptions<SizeContext> options) : IyuDbContext(options)
{
    public DbSet<SizeReading> Readings => Set<SizeReading>();
    public DbSet<SizeReadingExt> ReadingsExt => Set<SizeReadingExt>();
    public DbSet<SizeUnit> Units => Set<SizeUnit>();
    public DbSet<SizeUnitExt> UnitsExt => Set<SizeUnitExt>();
}

public sealed class SizeReadingsController(SizeContext ctx) : IyuODataController<SizeReadingExt, SizeReading>(ctx);
public sealed class SizeUnitsController(SizeContext ctx) : IyuODataController<SizeUnitExt, SizeUnit>(ctx);

/// <summary>
/// How many rows one <c>/$data</c> response may carry, and how large a <c>$top</c> may be asked for.
/// </summary>
/// <remarks>
/// Without a limit a request with no <c>$top</c> returns the whole set in one response — for a table
/// that grows without bound, one call occupies the database, the server's memory and the response.
/// These tests pin the finite defaults, a set's own override in both directions, and that a cut
/// response says so with <c>@odata.nextLink</c> rather than silently ending early.
/// </remarks>
public class ResultSizeLimitEndToEndTests
{
    private const string Readings = "SizeReadings";
    private const string Units = "SizeUnits";

    private static async Task<WebApplication> StartAsync(
        int rows, Action<IyuEdmModelBuilder> configure, bool withParent = false)
    {
        var dbName = "size-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddIyuMainServer<SizeContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(SizeReadingsController).Assembly);
                options.ODataModel.AddEntityPair<SizeReadingExt, SizeReading>(Readings);
                options.ODataModel.AddEntityPair<SizeUnitExt, SizeUnit>(Units);
                configure(options.ODataModel);
            });

        var app = builder.Build();
        app.UseIyuMainServer();

        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<SizeContext>();
            for (var i = 0; i < rows; i++)
            {
                ctx.ReadingsExt.Add(new SizeReadingExt { Id = Guid.NewGuid(), Seq = i });
                ctx.UnitsExt.Add(new SizeUnitExt { Id = Guid.NewGuid(), Seq = i });
            }
            // One more unit that owns every reading of a second batch — the parent an $expand
            // reaches a whole collection through.
            if (withParent)
            {
                var parent = new SizeUnitExt { Id = Guid.NewGuid(), Seq = -1 };
                for (var i = 0; i < rows; i++)
                    parent.Readings.Add(new SizeReadingExt { Id = Guid.NewGuid(), Seq = 1000 + i });
                ctx.UnitsExt.Add(parent);
            }
            await ctx.SaveChangesAsync();
        }

        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static int RowCount(JsonElement body) => body.GetProperty("value").GetArrayLength();

    private static string? NextLink(JsonElement body)
        => body.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;

    [Fact]
    public void The_defaults_are_finite()
    {
        var model = new IyuEdmModelBuilder();

        Assert.Equal(1000, model.DefaultMaxTop);
        Assert.Equal(1000, model.DefaultPageSize);
    }

    /// <summary>
    /// The reported case: no <c>$top</c>, more rows than a page. The response carries one page and
    /// a link to the next — and following the link yields the rest, so nothing is lost, only split.
    /// </summary>
    [Fact]
    public async Task A_result_larger_than_the_page_is_cut_and_says_where_the_rest_is()
    {
        await using var app = await StartAsync(rows: 7, model => model.DefaultPageSize = 3);
        using var client = app.GetTestClient();

        var (status, first) = await GetAsync(client, $"/$data/{Readings}?$orderby=Seq&$count=true");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, RowCount(first));
        Assert.Equal(7, first.GetProperty("@odata.count").GetInt32());
        var next = NextLink(first);
        Assert.NotNull(next);

        var seen = RowCount(first);
        while (next is not null)
        {
            var (_, page) = await GetAsync(client, next);
            seen += RowCount(page);
            next = NextLink(page);
        }
        Assert.Equal(7, seen);
    }

    /// <summary>A result that fits in one page is not given a link.</summary>
    [Fact]
    public async Task A_result_within_the_page_carries_no_link()
    {
        await using var app = await StartAsync(rows: 3, model => model.DefaultPageSize = 3);
        using var client = app.GetTestClient();

        var (_, body) = await GetAsync(client, $"/$data/{Readings}");

        Assert.Equal(3, RowCount(body));
        Assert.Null(NextLink(body));
    }

    /// <summary>
    /// A <c>$top</c> above the limit is refused, and refused for that reason — the negative control
    /// below keeps the status from standing in for the reason.
    /// </summary>
    [Fact]
    public async Task A_top_above_the_limit_is_refused_for_exceeding_it()
    {
        await using var app = await StartAsync(rows: 1, _ => { });
        using var client = app.GetTestClient();

        var (status, body) = await GetAsync(client, $"/$data/{Readings}?$top=1001");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("1000", body.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        var (withinStatus, _) = await GetAsync(client, $"/$data/{Readings}?$top=1000");
        Assert.Equal(HttpStatusCode.OK, withinStatus);
    }

    /// <summary>
    /// A set's own values replace the defaults for that set only — narrower for one, removed for
    /// another — each set answering by its own, not by the defaults or by its neighbour's.
    /// </summary>
    [Fact]
    public async Task A_set_can_narrow_or_lift_its_own_limit_without_touching_the_others()
    {
        await using var app = await StartAsync(rows: 5, model =>
        {
            model.DefaultPageSize = 4;
            model.Page(Readings, maxTop: 2, pageSize: 2);
            model.Page(Units, maxTop: null, pageSize: null);
        });
        using var client = app.GetTestClient();

        var (_, readings) = await GetAsync(client, $"/$data/{Readings}");
        Assert.Equal(2, RowCount(readings));
        Assert.NotNull(NextLink(readings));
        var (tooMany, _) = await GetAsync(client, $"/$data/{Readings}?$top=3");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany);

        var (_, units) = await GetAsync(client, $"/$data/{Units}");
        Assert.Equal(5, RowCount(units));
        Assert.Null(NextLink(units));
        var (unlimited, _) = await GetAsync(client, $"/$data/{Units}?$top=100000");
        Assert.Equal(HttpStatusCode.OK, unlimited);
    }

    /// <summary>Lifting the defaults restores one response for the whole set.</summary>
    [Fact]
    public async Task Null_defaults_return_the_whole_set_in_one_response()
    {
        await using var app = await StartAsync(rows: 5, model =>
        {
            model.DefaultMaxTop = null;
            model.DefaultPageSize = null;
        });
        using var client = app.GetTestClient();

        var (_, body) = await GetAsync(client, $"/$data/{Readings}?$top=100000");

        Assert.Equal(5, RowCount(body));
        Assert.Null(NextLink(body));
    }

    /// <summary>
    /// The limit is on the type, not on one route to it: a collection reached through another
    /// set's <c>$expand</c> is paged by its own set's size — otherwise expanding from a parent
    /// would be the way around the ceiling.
    /// </summary>
    [Fact]
    public async Task A_collection_reached_through_an_expand_is_paged_by_its_own_set()
    {
        await using var app = await StartAsync(rows: 5, model =>
        {
            model.Page(Readings, maxTop: 2, pageSize: 2);
            model.Page(Units, maxTop: null, pageSize: null);
        }, withParent: true);
        using var client = app.GetTestClient();

        var (status, body) = await GetAsync(client, $"/$data/{Units}?$filter=Seq eq -1&$expand=Readings");

        Assert.Equal(HttpStatusCode.OK, status);
        var parent = Assert.Single(body.GetProperty("value").EnumerateArray());
        Assert.Equal(2, parent.GetProperty("Readings").GetArrayLength());
    }

    [Fact]
    public void Paging_an_unregistered_set_is_refused()
    {
        var model = new IyuEdmModelBuilder();

        var error = Assert.Throws<InvalidOperationException>(() => model.Page("Nope", 10, 10));
        Assert.Contains("'Nope'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, null)]
    public void A_limit_that_is_not_positive_is_refused(int? maxTop, int? pageSize)
    {
        var model = new IyuEdmModelBuilder().AddEntityPair<SizeReadingExt, SizeReading>(Readings);

        Assert.Throws<ArgumentOutOfRangeException>(() => model.Page(Readings, maxTop, pageSize));
    }
}
