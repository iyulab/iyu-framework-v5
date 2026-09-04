using System.Net;
using System.Net.Http.Json;
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

// Top-level public types for the same reason ODataTestServerRoutingTests documents: a nested
// controller is not IsPublic and MVC's ControllerFeatureProvider skips it.

public sealed class ConflictWidget : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class ConflictWidgetExt : IyuEntity
{
    public string Name { get; set; } = "";
}

/// <summary>
/// Throws a <see cref="DbUpdateException"/> on every save — standing in for a real provider's
/// constraint violation (e.g. SQL Server/PostgreSQL both wrap a unique-index violation this way).
/// EF Core's own InMemory provider was tried first and rejected: a duplicate primary key across two
/// context instances does violate uniqueness, but InMemory leaks the collision as a raw internal
/// <see cref="ArgumentException"/> instead of wrapping it in <see cref="DbUpdateException"/> the way
/// every real relational provider does (confirmed empirically — a known InMemory-provider quirk,
/// unrelated to the handler logic these tests exist to verify). Forcing the exception directly tests
/// the handler's own mapping in isolation from that unrelated InMemory gap.
/// </summary>
public sealed class ConflictWidgetContext(DbContextOptions<ConflictWidgetContext> options) : IyuDbContext(options)
{
    public DbSet<ConflictWidget> Widgets => Set<ConflictWidget>();
    public DbSet<ConflictWidgetExt> WidgetsExt => Set<ConflictWidgetExt>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw new DbUpdateException("simulated-conflict-must-not-leak-to-the-client");
}

public sealed class ConflictWidgetsController(ConflictWidgetContext ctx)
    : IyuODataController<ConflictWidgetExt, ConflictWidget>(ctx);

public sealed class BoomWidget : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class BoomWidgetExt : IyuEntity
{
    public string Name { get; set; } = "";
}

/// <summary>Throws a plain, non-<see cref="DbUpdateException"/> failure on every save — a stand-in for any unexpected write-path exception that isn't a DB-constraint violation.</summary>
public sealed class BoomWidgetContext(DbContextOptions<BoomWidgetContext> options) : IyuDbContext(options)
{
    public DbSet<BoomWidget> Widgets => Set<BoomWidget>();
    public DbSet<BoomWidgetExt> WidgetsExt => Set<BoomWidgetExt>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated-unexpected-failure-must-not-leak-to-the-client");
}

public sealed class BoomWidgetsController(BoomWidgetContext ctx)
    : IyuODataController<BoomWidgetExt, BoomWidget>(ctx);

/// <summary>
/// docket G-1 (`ROADMAP.md` §2): <see cref="IyuODataController{TRead,TWrite}"/>'s write actions had
/// no structured error handling around <c>SaveChangesAsync</c> — any EF write failure surfaced as a
/// bare, unstructured 500. These tests drive real failures through the actual TestServer pipeline
/// (not a bare DI container) and assert on the wire response, the same depth
/// <see cref="RestrictPolicyEndToEndTests"/> holds authorization to.
/// </summary>
public class WriteExceptionHandlingEndToEndTests
{
    private const string ConflictSet = "ConflictWidgets";
    private const string BoomSet = "BoomWidgets";

    private static async Task<WebApplication> StartConflictAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<ConflictWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("conflict-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ConflictWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<ConflictWidgetExt, ConflictWidget>(ConflictSet);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartBoomAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<BoomWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("boom-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(BoomWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<BoomWidgetExt, BoomWidget>(BoomSet);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Post_that_raises_a_DbUpdateException_returns_409_problem_details_without_leaking_the_exception_message()
    {
        var app = await StartConflictAppAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .PostAsJsonAsync($"/$data/{ConflictSet}", new { Name = "irrelevant" });

            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("simulated-conflict-must-not-leak-to-the-client", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DbUpdateException", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Post_that_throws_an_unrelated_exception_returns_a_structured_500_without_leaking_the_message()
    {
        var app = await StartBoomAppAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient()
                .PostAsJsonAsync($"/$data/{BoomSet}", new { Name = "irrelevant" });

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("simulated-unexpected-failure-must-not-leak-to-the-client", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }
}
