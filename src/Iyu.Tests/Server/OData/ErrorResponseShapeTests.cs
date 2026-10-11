using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types, named for the sets they serve (see SharedKeyWriteContractEndToEndTests).

public sealed class TallyBook : IyuEntity
{
    public string Title { get; set; } = "";
}

public sealed class TallyBookExt : IyuEntity
{
    public string Title { get; set; } = "";
}

public sealed class TallyBookNote : IyuEntity
{
    public string Text { get; set; } = "";
}

public sealed class TallyBookNoteExt : IyuEntity
{
    public string Text { get; set; } = "";
}

public sealed class TallyBookSummary : IyuEntity
{
    public int Total { get; set; }
}

public sealed class TallyBookSummaryExt : IyuEntity
{
    public int Total { get; set; }
}

public sealed class TallyBookContext(DbContextOptions<TallyBookContext> options) : IyuDbContext(options)
{
    public DbSet<TallyBook> TallyBooks => Set<TallyBook>();
    public DbSet<TallyBookExt> TallyBooksExt => Set<TallyBookExt>();
    public DbSet<TallyBookNote> TallyBookNotes => Set<TallyBookNote>();
    public DbSet<TallyBookNoteExt> TallyBookNotesExt => Set<TallyBookNoteExt>();
    public DbSet<TallyBookSummary> TallyBookSummaries => Set<TallyBookSummary>();
    public DbSet<TallyBookSummaryExt> TallyBookSummariesExt => Set<TallyBookSummaryExt>();
}

public sealed class TallyBooksController(TallyBookContext ctx)
    : IyuODataController<TallyBookExt, TallyBook>(ctx);

public sealed class TallyBookNotesController(TallyBookContext ctx)
    : IyuODataController<TallyBookNoteExt, TallyBookNote>(ctx);

public sealed class TallyBookSummariesController(TallyBookContext ctx)
    : IyuODataController<TallyBookSummaryExt, TallyBookSummary>(ctx);

public static class NamespaceClashA
{
    public sealed class Twin : IyuEntity { }
}

public static class NamespaceClashB
{
    public sealed class Twin : IyuEntity { }
}

/// <summary>
/// The body each error point of one <c>/$data</c> route answers with.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal the generic controller makes is an OData error object whose <c>error.code</c> is one
/// of <see cref="ODataErrorCodes"/> — including the two <see cref="IyuEnableQueryAttribute"/> makes:
/// a query option it cannot apply, and a key that names no row.
/// </para>
/// <para>
/// Each point is one line — status, media type, body kind, code — so a change to any of them is a
/// one-line diff here, which is the list of what a consumer has to handle differently.
/// </para>
/// </remarks>
public class ErrorResponseShapeTests
{
    private const string Ledgers = "TallyBooks";
    private const string Notes = "TallyBookNotes";
    private const string Summaries = "TallyBookSummaries";

    private static async Task<WebApplication> StartAsync(
        Action<IyuEdmModelBuilder>? model = null,
        Action<IServiceCollection>? servicesFirst = null,
        Action<WebApplication>? endpoints = null)
    {
        var dbName = "errorshape-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        servicesFirst?.Invoke(builder.Services);

        builder.Services.AddIyuMainServer<TallyBookContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(TallyBooksController).Assembly);
                options.ODataModel.AddEntityPair<TallyBookExt, TallyBook>(Ledgers);
                options.ODataModel.AddEntityPair<TallyBookNoteExt, TallyBookNote>(Notes);
                options.ODataModel.AddEntityPair<TallyBookSummaryExt, TallyBookSummary>(
                    Summaries, ODataVerb.Post, ODataVerb.Patch, ODataVerb.Delete);
                options.ODataModel.DeclareSharedKey(Notes, Ledgers);
                model?.Invoke(options.ODataModel);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        endpoints?.Invoke(app);
        await app.StartAsync();
        return app;
    }

    private const string FailureText = "boom-must-not-reach-the-client";

    /// <summary>An endpoint under the OData route and one outside it, both failing with an unhandled exception.</summary>
    private static void MapFailures(WebApplication app)
    {
        app.MapGet("/$data/__boom", (Func<string>)(() => throw new InvalidOperationException(FailureText)));
        app.MapGet("/api/boom", (Func<string>)(() => throw new InvalidOperationException(FailureText)));
    }

    private static async Task<Guid> SeedBookAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TallyBookContext>();
        var id = Guid.NewGuid();
        ctx.TallyBooks.Add(new TallyBook { Id = id, Title = "q3" });
        ctx.TallyBooksExt.Add(new TallyBookExt { Id = id, Title = "q3" });
        await ctx.SaveChangesAsync();
        return id;
    }

    /// <summary>Describes a response as status · media type · body kind · the <c>error.code</c> value when there is one.</summary>
    private static async Task<string> ShapeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var media = response.Content.Headers.ContentType?.MediaType ?? "(none)";
        string kind;
        if (body.Length == 0)
        {
            kind = "empty";
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                    kind = "json-string";
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                    kind = "odata-error code=" + (error.TryGetProperty("code", out var code) ? $"'{code.GetString()}'" : "(absent)");
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@odata.context", out var context)
                         && root.TryGetProperty("value", out _))
                    kind = "odata-value " + context.GetString()![(context.GetString()!.IndexOf('#') + 1)..];
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("title", out _) && root.TryGetProperty("status", out _))
                    kind = "problem-details";
                else
                    kind = "json-other";
            }
            catch (JsonException)
            {
                kind = "text";
            }
        }
        return $"{(int)response.StatusCode} {media} {kind}";
    }

    [Fact]
    public async Task A_query_option_error()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/$data/{Ledgers}?$filter=Nope eq 1");

        Assert.Equal("400 application/json odata-error code='InvalidQuery'", await ShapeOf(response));
    }

    /// <summary>
    /// An unknown navigation is refused by the query layer. The authorization filter's own refusal of
    /// an <c>$expand</c> it cannot parse carries the same code; no request is known that reaches it
    /// before the query layer does, so it is not pinned here.
    /// </summary>
    [Fact]
    public async Task An_expand_naming_no_navigation()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/$data/{Ledgers}?$expand=Nope");

        Assert.Equal("400 application/json odata-error code='InvalidQuery'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_query_option_over_its_limit()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/$data/{Ledgers}?$top=100000");

        Assert.Equal("400 application/json odata-error code='InvalidQuery'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_query_option_error_on_a_single_entity()
    {
        await using var app = await StartAsync();
        var key = await SeedBookAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/$data/{Ledgers}({key})?$select=Nope");

        Assert.Equal("400 application/json odata-error code='InvalidQuery'", await ShapeOf(response));
    }

    /// <summary>
    /// Relabelling keeps the attribute's own message, and outside Development still carries no
    /// <c>innererror</c>.
    /// </summary>
    [Fact]
    public async Task A_query_option_error_keeps_its_message_and_no_diagnostics()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/$data/{Ledgers}?$filter=Nope eq 1");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Contains("Nope", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("innererror", out _));
    }

    [Fact]
    public async Task A_body_that_fails_model_validation()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync($"/$data/{Ledgers}", new { Title = 42 });

        Assert.Equal("400 application/json odata-error code='InvalidBody'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_patch_whose_every_property_is_unwritable()
    {
        await using var app = await StartAsync();
        var key = await SeedBookAsync(app);
        using var client = app.GetTestClient();

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/$data/{Ledgers}({key})")
        {
            Content = JsonContent.Create(new { CreatedAt = DateTimeOffset.UtcNow }),
        };
        using var response = await client.SendAsync(patch);

        Assert.Equal("400 application/json odata-error code='UnwritableProperty'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_verb_the_set_does_not_accept()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync($"/$data/{Summaries}", new { Total = 1 });

        Assert.Equal("405 application/json odata-error code='ReadOnlySet'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_shared_key_whose_row_already_exists()
    {
        await using var app = await StartAsync();
        var key = await SeedBookAsync(app);
        using var client = app.GetTestClient();

        using (var first = await client.PostAsJsonAsync($"/$data/{Notes}", new { Id = key, Text = "a" }))
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var response = await client.PostAsJsonAsync($"/$data/{Notes}", new { Id = key, Text = "b" });

        Assert.Equal("409 application/json odata-error code='SharedKeyRowExists'", await ShapeOf(response));
    }

    /// <summary>
    /// The model is published under a neutral namespace, not the read types' CLR namespace: neither
    /// <c>$metadata</c> nor a query error that names a type reveals how the application organises its
    /// code. The type names themselves are unchanged.
    /// </summary>
    [Fact]
    public async Task The_model_is_published_under_a_neutral_namespace()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var metadata = await client.GetStringAsync("/$data/$metadata");
        using var error = await client.GetAsync($"/$data/{Ledgers}?$filter=Nope eq 1");
        var message = await error.Content.ReadAsStringAsync();

        Assert.Contains("Namespace=\"Default\"", metadata);
        Assert.Contains("Default.TallyBookExt", metadata);
        Assert.DoesNotContain(typeof(TallyBookExt).Namespace!, metadata);
        Assert.Contains("Default.TallyBookExt", message);
        Assert.DoesNotContain(typeof(TallyBookExt).Namespace!, message);
    }

    /// <summary>A payload's <c>@odata.type</c>, where one is written, carries the same namespace.</summary>
    [Fact]
    public async Task A_payload_names_its_type_in_the_published_namespace()
    {
        await using var app = await StartAsync();
        await SeedBookAsync(app);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/$data/{Ledgers}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;odata.metadata=full");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"@odata.type\":\"#Default.TallyBookExt\"", body);
    }

    /// <summary><c>null</c> keeps the CLR namespaces — the behaviour before the setting existed.</summary>
    [Fact]
    public async Task A_null_namespace_keeps_the_CLR_namespaces()
    {
        await using var app = await StartAsync(model => model.Namespace = null);
        using var client = app.GetTestClient();

        var metadata = await client.GetStringAsync("/$data/$metadata");

        Assert.Contains($"{typeof(TallyBookExt).Namespace}.TallyBookExt", metadata);
    }

    /// <summary>
    /// Two exposed types with the same name cannot share one namespace; the model refuses to build and
    /// names them, rather than letting one shadow the other.
    /// </summary>
    [Fact]
    public void Two_types_that_would_share_a_name_are_refused_by_name()
    {
        var model = new IyuEdmModelBuilder();
        model.AddEntityPair<NamespaceClashA.Twin, NamespaceClashA.Twin>("TwinsA");
        model.AddEntityPair<NamespaceClashB.Twin, NamespaceClashB.Twin>("TwinsB");

        var refused = Assert.Throws<InvalidOperationException>(() => model.GetEdmModel());
        Assert.Contains("'Twin'", refused.Message);
    }

    [Fact]
    public async Task A_shared_key_post_without_a_key()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync($"/$data/{Notes}", new { Text = "a" });

        Assert.Equal("400 application/json odata-error code='SharedKeyRequired'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_shared_key_naming_no_principal()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync($"/$data/{Notes}", new { Id = Guid.NewGuid(), Text = "a" });

        Assert.Equal("409 application/json odata-error code='SharedKeyPrincipalMissing'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_key_that_names_no_row()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/$data/{Ledgers}({Guid.NewGuid()})");

        Assert.Equal("404 application/json odata-error code='KeyNotFound'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_patch_to_a_key_that_names_no_row()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/$data/{Ledgers}({Guid.NewGuid()})")
        {
            Content = JsonContent.Create(new { Title = "x" }),
        };
        using var response = await client.SendAsync(patch);

        Assert.Equal("404 application/json odata-error code='KeyNotFound'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_delete_of_a_key_that_names_no_row()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.DeleteAsync($"/$data/{Ledgers}({Guid.NewGuid()})");

        Assert.Equal("404 application/json odata-error code='KeyNotFound'", await ShapeOf(response));
    }

    /// <summary>A path naming no set is a route-level miss, distinct from <c>KeyNotFound</c>.</summary>
    [Fact]
    public async Task A_path_that_names_no_set()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/$data/Nope");

        Assert.Equal("404 application/json odata-error code='NotFound'", await ShapeOf(response));
    }

    [Fact]
    public async Task A_verb_nothing_on_the_path_answers()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PutAsJsonAsync($"/$data/{Ledgers}", new { Title = "x" });

        Assert.Equal("405 application/json odata-error code='MethodNotAllowed'", await ShapeOf(response));
    }

    /// <summary>A body-less failure outside the route stays as the host answers it.</summary>
    [Fact]
    public async Task A_path_off_the_route_that_names_nothing()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/nope");

        Assert.Equal("404 (none) empty", await ShapeOf(response));
    }

    /// <summary>
    /// A failure nothing classified is answered in the route's format, not as <c>application/problem+json</c> —
    /// the one kind of failure on this route a client reading <c>error.code</c> could not read before.
    /// </summary>
    [Fact]
    public async Task An_unhandled_exception_on_the_route()
    {
        await using var app = await StartAsync(endpoints: MapFailures);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/$data/__boom");

        Assert.Equal("500 application/json odata-error code='InternalServerError'", await ShapeOf(response));
    }

    /// <summary>Outside the OData route the host keeps the default <c>ProblemDetails</c>.</summary>
    [Fact]
    public async Task An_unhandled_exception_off_the_route()
    {
        await using var app = await StartAsync(endpoints: MapFailures);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/boom");

        Assert.Equal("500 application/problem+json problem-details", await ShapeOf(response));
    }

    /// <summary>The answer names the request in the server's logs and says nothing about the exception.</summary>
    [Fact]
    public async Task An_unhandled_exception_names_the_request_and_not_the_exception()
    {
        await using var app = await StartAsync(endpoints: MapFailures);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/$data/__boom");
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrEmpty(error.GetProperty("innererror").GetProperty("traceId").GetString()));
        Assert.DoesNotContain(FailureText, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
    }

    /// <summary>The headers a client negotiates on are those of every other error on the route.</summary>
    [Fact]
    public async Task An_unhandled_exception_carries_the_route_s_headers()
    {
        await using var app = await StartAsync(endpoints: MapFailures);
        using var client = app.GetTestClient();

        using var refusal = await client.GetAsync($"/$data/{Ledgers}({Guid.NewGuid()})");
        using var failure = await client.GetAsync("/$data/__boom");

        Assert.Equal(refusal.Content.Headers.ContentType?.ToString(), failure.Content.Headers.ContentType?.ToString());
        Assert.Equal(refusal.Headers.GetValues("OData-Version"), failure.Headers.GetValues("OData-Version"));
    }

    /// <summary>A <c>code</c> the host puts on the problem is the error's code.</summary>
    [Fact]
    public async Task A_code_the_host_supplies_is_the_error_code()
    {
        await using var app = await StartAsync(
            servicesFirst: s => s.AddProblemDetails(o => o.CustomizeProblemDetails = c => c.ProblemDetails.Extensions["code"] = "Unavailable"),
            endpoints: MapFailures);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/$data/__boom");

        Assert.Equal("500 application/json odata-error code='Unavailable'", await ShapeOf(response));
    }

    /// <summary>
    /// A host that called <c>AddProblemDetails()</c> before <c>AddIyuMainServer</c> — which registers ASP.NET Core's
    /// default writer first — still gets the route's format.
    /// </summary>
    [Fact]
    public async Task A_host_that_adds_problem_details_first_still_gets_the_route_format()
    {
        await using var app = await StartAsync(servicesFirst: s => s.AddProblemDetails(), endpoints: MapFailures);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/$data/__boom");

        Assert.Equal("500 application/json odata-error code='InternalServerError'", await ShapeOf(response));
    }

    /// <summary>
    /// A writer the host registers before <c>AddIyuMainServer</c> answers first — the same ordering rule the
    /// exception handlers follow — so an application with one error format for every surface keeps it.
    /// </summary>
    [Fact]
    public async Task A_writer_registered_first_answers_on_the_route()
    {
        await using var app = await StartAsync(
            servicesFirst: s => s.AddSingleton<IProblemDetailsWriter, HostErrorWriter>(),
            endpoints: MapFailures);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/$data/__boom");

        Assert.Equal("500 application/json json-other", await ShapeOf(response));
    }

    /// <summary>
    /// The body-less failures take the same path: status-code pages write through the problem-details service, so a
    /// writer registered first answers them too — an application's one error format covers the route's 404 and 405.
    /// </summary>
    [Theory]
    [InlineData("GET", "/$data/Nope", "404 application/json json-other")]
    [InlineData("PUT", "/$data/" + Ledgers, "405 application/json json-other")]
    public async Task A_writer_registered_first_answers_the_route_s_body_less_failures(string method, string path,
        string expected)
    {
        await using var app = await StartAsync(servicesFirst: s => s.AddSingleton<IProblemDetailsWriter, HostErrorWriter>());
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "PUT" ? JsonContent.Create(new { Title = "x" }) : null,
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(expected, await ShapeOf(response));
    }

    private sealed class HostErrorWriter : IProblemDetailsWriter
    {
        public bool CanWrite(ProblemDetailsContext context) => true;

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            context.HttpContext.Response.ContentType = "application/json";
            return new(context.HttpContext.Response.WriteAsync("""{"fault":"host"}"""));
        }
    }
}
