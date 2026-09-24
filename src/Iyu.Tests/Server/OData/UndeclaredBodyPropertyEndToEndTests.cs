using System.Net;
using System.Text;
using System.Text.Json;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// A write body that names a property the set's type does not declare is refused with that name,
/// and the refusal carries nothing else — no EDM type name, and no second error claiming the body
/// was missing.
/// </summary>
/// <remarks>
/// <para>
/// OData's reader refuses such a body by throwing, and its message names the EDM type as well as
/// the property. Before this, the sanitizer replaced that message wholesale, so the caller learned
/// only that "the value could not be converted" — and, because the failed bind leaves the body
/// parameter null, MVC's implicit <c>[Required]</c> added <i>"The body field is required."</i>
/// beside it, for a request that did carry a body.
/// </para>
/// <para>
/// Every assertion is on the wire response of a real pipeline. The fixtures are the ones the
/// binding and navigation tests already use, so the sets are ordinary ones.
/// </para>
/// </remarks>
public class UndeclaredBodyPropertyEndToEndTests
{
    private static async Task<WebApplication> StartWidgetsAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<BindingWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("undeclared-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(BindingWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<BindingWidgetExt, BindingWidget>("BindingWidgets");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartVesselsAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<VesselContext>(
            configureDb: db => db.UseInMemoryDatabase("undeclared-nav-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(VesselsController).Assembly);
                options.ODataModel.AddEntityPair<VesselExt, Vessel>("Vessels");
                options.ODataModel.AddEntityPair<VesselSurveyExt, VesselSurvey>("VesselSurveys");
                options.ODataModel.AddEntityPair<VesselCrewMemberExt, VesselCrewMember>("VesselCrewMembers");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<Guid> SeedWidgetAsync(WebApplication app)
    {
        var id = Guid.NewGuid();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BindingWidgetContext>();
        db.Widgets.Add(new BindingWidget { Id = id, Label = "seed" });
        await db.SaveChangesAsync();
        return id;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record Refusal(HttpStatusCode Status, string Code, IReadOnlyList<(string? Target, string Message)> Details, string Raw);

    private static async Task<Refusal> ReadAsync(HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var error = doc.RootElement.GetProperty("error");
        var details = error.TryGetProperty("details", out var d)
            ? d.EnumerateArray()
                .Select(e => (e.TryGetProperty("target", out var t) ? t.GetString() : null, e.GetProperty("message").GetString()!))
                .ToList()
            : [];
        return new Refusal(resp.StatusCode, error.GetProperty("code").GetString()!, details, raw);
    }

    [Fact]
    public async Task Post_naming_an_undeclared_property_is_refused_with_that_name_as_the_target()
    {
        var app = await StartWidgetsAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/BindingWidgets", Json("""{"Label":"x","Nmae":"y"}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
            Assert.Equal(ODataErrorCodes.UnknownProperty, refusal.Code);
            var detail = Assert.Single(refusal.Details);
            Assert.Equal("Nmae", detail.Target);
            Assert.Contains("'Nmae'", detail.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(BindingWidgetExt), refusal.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("required", refusal.Raw, StringComparison.OrdinalIgnoreCase);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Patch_names_every_undeclared_property_not_only_the_first_one_the_reader_hit()
    {
        var app = await StartWidgetsAsync();
        try
        {
            var id = await SeedWidgetAsync(app);
            using var resp = await app.GetTestServer().CreateClient().PatchAsync(
                $"/$data/BindingWidgets({id})", Json("""{"Label":"z","Nmae":"y","Colour":1}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
            Assert.Equal(ODataErrorCodes.UnknownProperty, refusal.Code);
            Assert.Equal(["Colour", "Nmae"], refusal.Details.Select(d => d.Target).Order());
            Assert.DoesNotContain("delta", refusal.Raw, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task An_undeclared_name_inside_a_nested_value_is_reported_by_its_path()
    {
        var app = await StartVesselsAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/Vessels",
                Json("""{"Name":"a","Survey":{"Grde":"A"},"Crew":[{"Rank":"x"},{"Rnak":"y"}]}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
            Assert.Equal(ODataErrorCodes.UnknownProperty, refusal.Code);
            Assert.Equal(["Crew.Rnak", "Survey.Grde"], refusal.Details.Select(d => d.Target).Order());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_declared_name_in_another_case_is_not_reported()
    {
        // The body reader binds names without regard to case — `label` binds to `Label`, which is
        // what a camelCase JSON serializer sends. Reporting it would name a property that bound.
        var app = await StartWidgetsAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/BindingWidgets", Json("""{"label":"x","nmae":"y"}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(ODataErrorCodes.UnknownProperty, refusal.Code);
            Assert.Equal("nmae", Assert.Single(refusal.Details).Target);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Annotations_are_not_mistaken_for_undeclared_properties()
    {
        // The type annotation names a type the model does not have, so the bind fails — but the
        // key that carries it is OData vocabulary, not a property, and must not be reported as one.
        var app = await StartWidgetsAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/BindingWidgets", Json("""{"@odata.type":"#Nowhere.Nothing","Label":"x"}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
            Assert.Equal(ODataErrorCodes.InvalidBody, refusal.Code);
            Assert.DoesNotContain(refusal.Details, d => d.Target == "@odata.type");
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_malformed_value_is_one_error_not_two()
    {
        var app = await StartWidgetsAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/BindingWidgets", Json("""{"OccurredAt":"2026-08-19 10:00","Label":"x"}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
            Assert.Equal(ODataErrorCodes.InvalidBody, refusal.Code);
            Assert.Single(refusal.Details);
            Assert.DoesNotContain("body field is required", refusal.Raw, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task An_empty_body_is_refused_as_InvalidBody_once()
    {
        // The [FromBody] binder refuses an empty body before the action runs, so the action's own
        // null check is not what answers it — the binder's error is, under the same code.
        var app = await StartWidgetsAsync();
        try
        {
            var id = await SeedWidgetAsync(app);
            var client = app.GetTestServer().CreateClient();
            using var post = await client.PostAsync("/$data/BindingWidgets", Json(""));
            using var patch = await client.PatchAsync($"/$data/BindingWidgets({id})", Json(""));

            foreach (var refusal in new[] { await ReadAsync(post), await ReadAsync(patch) })
            {
                Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
                Assert.Equal(ODataErrorCodes.InvalidBody, refusal.Code);
                var detail = Assert.Single(refusal.Details);
                Assert.Contains("non-empty request body", detail.Message, StringComparison.Ordinal);
            }
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_body_that_binds_still_reports_its_validation_errors_by_property()
    {
        // Control: dropping the body parameter's own entry must not drop real validation errors.
        var app = await StartWidgetsAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/BindingWidgets", Json("""{"OccurredAt":"2026-08-19T10:00:00Z"}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
            Assert.Equal(ODataErrorCodes.InvalidBody, refusal.Code);
            Assert.Contains(refusal.Details, d => d.Target == "Label");
        }
        finally { await app.DisposeAsync(); }
    }
}
