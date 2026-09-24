using System.Net;
using System.Net.Http.Json;
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
/// The body each error point of one <c>/$data</c> route answers with today — pinned as it is, not
/// as it should be.
/// </summary>
/// <remarks>
/// <para>
/// A caller handling errors from this surface has to parse several shapes: an OData error object for
/// most refusals — with an empty <c>error.code</c>, except the shared-key conflict, which carries the
/// status as its code — an OData primitive value for a verb the set does not accept, and no body at
/// all for a missing key. Making them one shape with a closed set of codes is a breaking change to
/// every one of these points, and it can only be designed against what they actually return, which
/// nothing had measured.
/// </para>
/// <para>
/// So these assertions describe the current contract, deliberately. When the shapes are unified,
/// this file is what changes, and its diff is the list of what a consumer has to handle differently.
/// </para>
/// </remarks>
public class ErrorResponseShapeTests
{
    private const string Ledgers = "TallyBooks";
    private const string Notes = "TallyBookNotes";
    private const string Summaries = "TallyBookSummaries";

    private static async Task<WebApplication> StartAsync(Action<IyuEdmModelBuilder>? model = null)
    {
        var dbName = "errorshape-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();

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
        await app.StartAsync();
        return app;
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

        Assert.Equal("400 application/json odata-error code=''", await ShapeOf(response));
    }

    [Fact]
    public async Task A_body_that_fails_model_validation()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync($"/$data/{Ledgers}", new { Title = 42 });

        Assert.Equal("400 application/json odata-error code=''", await ShapeOf(response));
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

        Assert.Equal("400 application/json odata-error code=''", await ShapeOf(response));
    }

    [Fact]
    public async Task A_verb_the_set_does_not_accept()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync($"/$data/{Summaries}", new { Total = 1 });

        Assert.Equal("405 application/json odata-value Edm.String", await ShapeOf(response));
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

        Assert.Equal("409 application/json odata-error code='409'", await ShapeOf(response));
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
    public async Task A_key_that_names_no_row()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/$data/{Ledgers}({Guid.NewGuid()})");

        Assert.Equal("404 (none) empty", await ShapeOf(response));
    }
}
