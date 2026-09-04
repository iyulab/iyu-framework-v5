using System.Net;
using System.Net.Http.Json;
using System.Text;
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

public sealed class BindingWidget : IyuEntity
{
    public DateTimeOffset OccurredAt { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    public string Label { get; set; } = "";
}

public sealed class BindingWidgetExt : IyuEntity
{
    public DateTimeOffset OccurredAt { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    public string Label { get; set; } = "";
}

public sealed class BindingWidgetContext(DbContextOptions<BindingWidgetContext> options) : IyuDbContext(options)
{
    public DbSet<BindingWidget> Widgets => Set<BindingWidget>();
    public DbSet<BindingWidgetExt> WidgetsExt => Set<BindingWidgetExt>();
}

public sealed class BindingWidgetsController(BindingWidgetContext ctx)
    : IyuODataController<BindingWidgetExt, BindingWidget>(ctx);

/// <summary>
/// docket G-2 (`ROADMAP.md` §2), redirected: a malformed EDM literal (e.g. a
/// <c>DateTimeOffset</c> string with no offset) makes OData's own body binder fail the whole
/// <c>[FromBody]</c> parameter, not just that one property — `IyuODataController.Post`/`Patch`
/// checked <c>body is null</c>/<c>delta is null</c> before ever looking at <c>ModelState</c>, so the
/// binder's own error (which — confirmed by direct inspection — DOES land in <c>ModelState</c>,
/// carrying the raw <c>ODataException</c> message with the internal <c>Edm.*</c> type name) was
/// discarded outright and the client got a completely empty 400 instead. These tests drive real
/// failures through the actual TestServer pipeline and assert on the wire response.
/// </summary>
public class WriteBindingErrorSanitizationEndToEndTests
{
    private const string Set = "BindingWidgets";

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<BindingWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("binding-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(BindingWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<BindingWidgetExt, BindingWidget>(Set);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_with_a_malformed_EDM_literal_returns_400_with_a_message_and_no_internal_type_name()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                $"/$data/{Set}", Json("""{"OccurredAt":"2026-08-19 10:00","Label":"x"}"""));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body));
            Assert.DoesNotContain("Edm.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ODataException", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Post_missing_a_required_field_still_returns_the_ordinary_validation_message()
    {
        var app = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                $"/$data/{Set}", Json("""{"OccurredAt":"2026-08-19T10:00:00Z"}"""));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Label", body, StringComparison.Ordinal);
            Assert.Contains("required", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Patch_with_a_malformed_EDM_literal_returns_400_with_a_message_and_no_internal_type_name()
    {
        var app = await StartAsync();
        try
        {
            var id = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BindingWidgetContext>();
                db.Widgets.Add(new BindingWidget { Id = id, Label = "seed" });
                await db.SaveChangesAsync();
            }

            using var resp = await app.GetTestServer().CreateClient().PatchAsync(
                $"/$data/{Set}({id})", Json("""{"OccurredAt":"2026-08-19 10:00"}"""));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body));
            Assert.DoesNotContain("Edm.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ODataException", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }
}
