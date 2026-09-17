using System.Net;
using System.Runtime.Serialization;
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

public enum LineKind
{
    [EnumMember(Value = "product")] Product,
    [EnumMember(Value = "print_order")] PrintOrder,
    [EnumMember(Value = "service")] Service,
}

public sealed class FilterLine : IyuEntity
{
    public string Name { get; set; } = "";
    public LineKind Kind { get; set; }
    public LineKind? OptionalKind { get; set; }
}

public sealed class FilterLineContext(DbContextOptions<FilterLineContext> options) : IyuDbContext(options)
{
    public DbSet<FilterLine> Lines => Set<FilterLine>();
}

public sealed class FilterLinesController(FilterLineContext ctx)
    : IyuODataController<FilterLine, FilterLine>(ctx);

/// <summary>
/// <c>$filter</c> over an enum whose members carry <see cref="EnumMemberAttribute"/>: the EDM
/// names those members by their wire value, and every operator has to accept that spelling.
/// </summary>
/// <remarks>
/// <c>eq</c> resolved the wire value through the model's CLR member map, while <c>in</c> parsed
/// each collection item as a CLR member <i>name</i> and threw — so the same value worked in one
/// operator and was a 500 in the other.
/// </remarks>
public class EnumInFilterEndToEndTests
{
    private const string Set = "FilterLines";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "enum-in-" + Guid.NewGuid().ToString("N");
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
            new FilterLine { Id = Guid.NewGuid(), Name = "c", Kind = LineKind.Service, OptionalKind = LineKind.Service });
        await db.SaveChangesAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string[] Names)> QueryAsync(WebApplication app, string filter)
    {
        var client = app.GetTestServer().CreateClient();
        using var response = await client.GetAsync($"/$data/{Set}?$filter={Uri.EscapeDataString(filter)}&$orderby=Name");
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, []);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("Name").GetString()!)
            .ToArray();
        return (response.StatusCode, names);
    }

    /// <summary>The control: <c>eq</c> already accepted the wire value.</summary>
    [Fact]
    public async Task Eq_accepts_the_wire_value()
    {
        var app = await StartAsync();
        try
        {
            var (status, names) = await QueryAsync(app, "Kind eq 'print_order'");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(["b"], names);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task In_accepts_the_same_wire_values_as_eq()
    {
        var app = await StartAsync();
        try
        {
            var (status, names) = await QueryAsync(app, "Kind in ('product','print_order')");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(["a", "b"], names);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task In_over_a_nullable_enum_accepts_wire_values_and_null()
    {
        var app = await StartAsync();
        try
        {
            var (status, names) = await QueryAsync(app, "OptionalKind in ('service',null)");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(["b", "c"], names);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A value the enum does not declare is the caller's mistake, never a server error.</summary>
    [Theory]
    [InlineData("Kind in ('product','nope')")]
    [InlineData("Kind in ('PrintOrder')")] // the CLR name is not the wire name
    [InlineData("Kind eq 'nope'")]
    public async Task An_undeclared_value_is_a_bad_request(string filter)
    {
        var app = await StartAsync();
        try
        {
            var (status, _) = await QueryAsync(app, filter);
            Assert.Equal(HttpStatusCode.BadRequest, status);
        }
        finally { await app.DisposeAsync(); }
    }
}
