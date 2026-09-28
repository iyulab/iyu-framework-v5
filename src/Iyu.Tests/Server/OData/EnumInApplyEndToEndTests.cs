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

// A second fixture for a groupby over a navigation path, whose result nests one wrapper in another.
// Top-level public types for the reason ODataTestServerRoutingTests documents.

public sealed class ApplyCategory : IyuEntity { public LineKind Kind { get; set; } }
public sealed class ApplyCategoryExt : IyuEntity { public LineKind Kind { get; set; } }
public sealed class ApplyItem : IyuEntity { public Guid CategoryId { get; set; } }

public sealed class ApplyItemExt : IyuEntity
{
    public Guid CategoryId { get; set; }
    public ApplyCategoryExt Category { get; set; } = null!;
}

public sealed class ApplyContext(DbContextOptions<ApplyContext> options) : IyuDbContext(options)
{
    public DbSet<ApplyCategory> Categories => Set<ApplyCategory>();
    public DbSet<ApplyCategoryExt> CategoryExts => Set<ApplyCategoryExt>();
    public DbSet<ApplyItem> Items => Set<ApplyItem>();
    public DbSet<ApplyItemExt> ItemExts => Set<ApplyItemExt>();
}

public sealed class ApplyItemsController(ApplyContext ctx) : IyuODataController<ApplyItemExt, ApplyItem>(ctx);

/// <summary>
/// <c>$apply</c> results over an enum whose members carry
/// <see cref="System.Runtime.Serialization.EnumMemberAttribute"/> speak the same wire value as every
/// other read of that property.
/// </summary>
/// <remarks>
/// An entity read wrote <c>print_order</c> while a <c>groupby</c> over the same property wrote the CLR
/// member name <c>PrintOrder</c>: a label table built from one did not match the other, and a grouped
/// value sent back as a <c>$filter</c> was rejected. Reuses <see cref="EnumInFilterEndToEndTests"/>'
/// entity and controller.
/// </remarks>
public class EnumInApplyEndToEndTests
{
    private const string Set = "FilterLines";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "enum-apply-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<FilterLineContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(FilterLinesController).Assembly);
                options.ODataModel.AddEntityPair<FilterLine, FilterLine>(Set);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FilterLineContext>();
        db.Lines.AddRange(
            new FilterLine { Id = Guid.NewGuid(), Name = "a", Kind = LineKind.Product, OptionalKind = LineKind.Product },
            new FilterLine { Id = Guid.NewGuid(), Name = "b", Kind = LineKind.PrintOrder, OptionalKind = null },
            new FilterLine { Id = Guid.NewGuid(), Name = "c", Kind = LineKind.PrintOrder, OptionalKind = LineKind.Service });
        await db.SaveChangesAsync();
        return app;
    }

    private static async Task<JsonElement[]> GetValuesAsync(WebApplication app, string query)
    {
        var client = app.GetTestServer().CreateClient();
        using var response = await client.GetAsync($"/$data/{Set}?{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static string? Text(JsonElement row, string name) =>
        row.GetProperty(name) is { ValueKind: JsonValueKind.Null } ? null : row.GetProperty(name).GetString();

    [Fact]
    public async Task Groupby_writes_the_wire_value_select_writes()
    {
        var app = await StartAsync();
        try
        {
            var selected = await GetValuesAsync(app, "$select=Kind&$orderby=Name");
            var grouped = await GetValuesAsync(app, "$apply=" + Uri.EscapeDataString("groupby((Kind),aggregate($count as C))"));

            Assert.Equal(
                selected.Select(r => Text(r, "Kind")).Distinct().Order(StringComparer.Ordinal),
                grouped.Select(r => Text(r, "Kind")).Order(StringComparer.Ordinal));
            Assert.Contains(grouped, r => Text(r, "Kind") == "print_order" && r.GetProperty("C").GetInt32() == 2);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Groupby_over_a_nullable_enum_writes_wire_values_and_null()
    {
        var app = await StartAsync();
        try
        {
            var grouped = await GetValuesAsync(app, "$apply=" + Uri.EscapeDataString("groupby((OptionalKind),aggregate($count as C))"));

            Assert.Equal(
                new string?[] { null, "product", "service" },
                grouped.Select(r => Text(r, "OptionalKind")).Order(StringComparer.Ordinal));
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The drill-down: a grouped value sent back as a filter selects that group's rows.</summary>
    [Fact]
    public async Task A_grouped_value_round_trips_through_filter()
    {
        var app = await StartAsync();
        try
        {
            var grouped = await GetValuesAsync(app, "$apply=" + Uri.EscapeDataString("groupby((Kind),aggregate($count as C))"));
            foreach (var group in grouped)
            {
                var value = Text(group, "Kind")!;
                var rows = await GetValuesAsync(app, "$filter=" + Uri.EscapeDataString($"Kind eq '{value}'"));
                Assert.Equal(group.GetProperty("C").GetInt32(), rows.Length);
            }
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Groupby_over_a_navigation_path_writes_the_wire_value()
    {
        var dbName = "enum-apply-nav-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<ApplyContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ApplyItemsController).Assembly);
                options.ODataModel.AddEntityPair<ApplyItemExt, ApplyItem>("ApplyItems");
                options.ODataModel.AddEntityPair<ApplyCategoryExt, ApplyCategory>("ApplyCategories");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplyContext>();
                var print = new ApplyCategoryExt { Id = Guid.NewGuid(), Kind = LineKind.PrintOrder };
                var product = new ApplyCategoryExt { Id = Guid.NewGuid(), Kind = LineKind.Product };
                db.CategoryExts.AddRange(print, product);
                db.ItemExts.AddRange(
                    new ApplyItemExt { Id = Guid.NewGuid(), CategoryId = print.Id },
                    new ApplyItemExt { Id = Guid.NewGuid(), CategoryId = print.Id },
                    new ApplyItemExt { Id = Guid.NewGuid(), CategoryId = product.Id });
                await db.SaveChangesAsync();
            }

            var client = app.GetTestServer().CreateClient();
            using var response = await client.GetAsync(
                "/$data/ApplyItems?$apply=" + Uri.EscapeDataString("groupby((Category/Kind),aggregate($count as C))"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var kinds = doc.RootElement.GetProperty("value").EnumerateArray()
                .ToDictionary(r => r.GetProperty("Category").GetProperty("Kind").GetString()!, r => r.GetProperty("C").GetInt32());
            Assert.Equal(new Dictionary<string, int> { ["print_order"] = 2, ["product"] = 1 }, kinds);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Filter_then_groupby_writes_the_wire_value()
    {
        var app = await StartAsync();
        try
        {
            var grouped = await GetValuesAsync(app,
                "$apply=" + Uri.EscapeDataString("filter(Kind eq 'print_order')/groupby((Kind,Name))"));

            Assert.All(grouped, r => Assert.Equal("print_order", Text(r, "Kind")));
            Assert.Equal(2, grouped.Length);
        }
        finally { await app.DisposeAsync(); }
    }
}
