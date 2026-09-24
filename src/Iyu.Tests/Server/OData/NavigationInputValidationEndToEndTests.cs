using System.ComponentModel.DataAnnotations;
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

// Top-level public types, named for the sets they serve (see SharedKeyWriteContractEndToEndTests
// for why every fixture's set names are its own).
//
// The read types declare their relationships the way EF Core's documentation recommends for a
// required navigation under nullable reference types: a non-nullable property initialised with
// `null!`. That declaration is honest about the relationship, and it is also — to ASP.NET Core's
// model validation — an implicitly required input.

public sealed class Vessel : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class VesselExt : IyuEntity
{
    public string Name { get; set; } = "";

    public VesselSurveyExt? Survey { get; set; }

    public ICollection<VesselCrewMemberExt> Crew { get; set; } = [];
}

public sealed class VesselSurvey : IyuEntity
{
    public string Grade { get; set; } = "";
}

public sealed class VesselSurveyExt : IyuEntity
{
    [MaxLength(2)]
    public string Grade { get; set; } = "";

    public VesselExt Vessel { get; set; } = null!;
}

public sealed class VesselCrewMember : IyuEntity
{
    public Guid VesselId { get; set; }

    public string Rank { get; set; } = "";
}

public sealed class VesselCrewMemberExt : IyuEntity
{
    public Guid VesselId { get; set; }

    public string Rank { get; set; } = "";

    public VesselExt Vessel { get; set; } = null!;
}

public sealed class VesselContext(DbContextOptions<VesselContext> options) : IyuDbContext(options)
{
    public DbSet<Vessel> Vessels => Set<Vessel>();
    public DbSet<VesselExt> VesselsExt => Set<VesselExt>();
    public DbSet<VesselSurvey> VesselSurveys => Set<VesselSurvey>();
    public DbSet<VesselSurveyExt> VesselSurveysExt => Set<VesselSurveyExt>();
    public DbSet<VesselCrewMember> VesselCrewMembers => Set<VesselCrewMember>();
    public DbSet<VesselCrewMemberExt> VesselCrewMembersExt => Set<VesselCrewMemberExt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<VesselExt>()
            .HasOne(v => v.Survey)
            .WithOne(s => s.Vessel)
            .HasForeignKey<VesselSurveyExt>(s => s.Id);

        modelBuilder.Entity<VesselExt>()
            .HasMany(v => v.Crew)
            .WithOne(c => c.Vessel)
            .HasForeignKey(c => c.VesselId);
    }
}

public sealed class VesselsController(VesselContext ctx)
    : IyuODataController<VesselExt, Vessel>(ctx);

public sealed class VesselSurveysController(VesselContext ctx)
    : IyuODataController<VesselSurveyExt, VesselSurvey>(ctx);

public sealed class VesselCrewMembersController(VesselContext ctx)
    : IyuODataController<VesselCrewMemberExt, VesselCrewMember>(ctx);

/// <summary>
/// What the generic write path validates when the read type it binds a request body to carries
/// navigation properties.
/// </summary>
/// <remarks>
/// <para>
/// POST and PATCH bind the body as the <em>read</em> type. A navigation on that type is part of the
/// read shape — it is filled by <c>$expand</c>, never by a create or an update, which copies scalar
/// properties to the write entity and nothing else. A body therefore never carries one, and the
/// write path must not demand one: a non-nullable navigation would otherwise turn every well-formed
/// create into a 400 naming a property the caller cannot meaningfully supply.
/// </para>
/// <para>
/// The control matters as much as the measurement. Excluding navigations from validation is only
/// correct if scalar validation still runs on the same body, so a refusal sits beside the
/// acceptances.
/// </para>
/// </remarks>
public class NavigationInputValidationEndToEndTests
{
    private const string Vessels = "Vessels";
    private const string Surveys = "VesselSurveys";
    private const string Crew = "VesselCrewMembers";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "navinput-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddIyuMainServer<VesselContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(VesselsController).Assembly);
                options.ODataModel.AddEntityPair<VesselExt, Vessel>(Vessels);
                options.ODataModel.AddEntityPair<VesselSurveyExt, VesselSurvey>(Surveys);
                options.ODataModel.AddEntityPair<VesselCrewMemberExt, VesselCrewMember>(Crew);
                options.ODataModel.DeclareSharedKey(Surveys, Vessels);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<Guid> SeedVesselAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<VesselContext>();
        var id = Guid.NewGuid();
        ctx.Vessels.Add(new Vessel { Id = id, Name = "tender" });
        ctx.VesselsExt.Add(new VesselExt { Id = id, Name = "tender" });
        await ctx.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// The reported shape: a one-to-one dependent whose key is its principal's key, with a
    /// non-nullable navigation back to that principal.
    /// </summary>
    [Fact]
    public async Task A_one_to_one_dependent_is_created_without_its_navigation_in_the_body()
    {
        await using var app = await StartAsync();
        var vessel = await SeedVesselAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Surveys}", new { Id = vessel, Grade = "A" });

        Assert.Equal(HttpStatusCode.Created, await StatusWithBodyAsync(response));
    }

    /// <summary>
    /// The same rule for the ordinary many-to-one shape, where the key is the row's own and the
    /// relationship is carried by a foreign key property.
    /// </summary>
    [Fact]
    public async Task A_many_to_one_dependent_is_created_with_its_foreign_key_alone()
    {
        await using var app = await StartAsync();
        var vessel = await SeedVesselAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Crew}", new { VesselId = vessel, Rank = "bosun" });

        Assert.Equal(HttpStatusCode.Created, await StatusWithBodyAsync(response));
    }

    /// <summary>
    /// A principal carrying both a reference and a collection navigation is created from its
    /// scalars.
    /// </summary>
    [Fact]
    public async Task A_principal_with_reference_and_collection_navigations_is_created_from_its_scalars()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Vessels}", new { Name = "launch" });

        Assert.Equal(HttpStatusCode.Created, await StatusWithBodyAsync(response));
    }

    /// <summary>
    /// Control: scalar validation still runs on the same body. The refusal names the scalar and
    /// does not name the navigation beside it.
    /// </summary>
    [Fact]
    public async Task An_invalid_scalar_is_still_refused_and_the_navigation_is_not_named()
    {
        await using var app = await StartAsync();
        var vessel = await SeedVesselAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Surveys}", new { Id = vessel, Grade = "AAA" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var targets = await ErrorTargetsAsync(response);
        Assert.Contains(nameof(VesselSurveyExt.Grade), targets);
        Assert.DoesNotContain(nameof(VesselSurveyExt.Vessel), targets);
    }

    /// <summary>
    /// PATCH validates only what the caller sent, so a navigation was never in its way — pinned so
    /// that a change to how navigations are excluded cannot quietly start validating them there.
    /// </summary>
    [Fact]
    public async Task A_patch_of_a_dependent_scalar_is_accepted()
    {
        await using var app = await StartAsync();
        var vessel = await SeedVesselAsync(app);
        using var client = app.GetTestClient();

        using (var created = await client.PostAsJsonAsync($"/$data/{Surveys}", new { Id = vessel, Grade = "A" }))
            Assert.Equal(HttpStatusCode.Created, await StatusWithBodyAsync(created));

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/$data/{Surveys}({vessel})")
        {
            Content = JsonContent.Create(new { Grade = "B" }),
        };
        using var response = await client.SendAsync(patch);

        Assert.Equal(HttpStatusCode.NoContent, await StatusWithBodyAsync(response));
    }

    /// <summary>
    /// Returns the status, but on an unexpected 400 fails with the body so a regression reads as
    /// the property the server objected to rather than as a bare status mismatch.
    /// </summary>
    private static async Task<HttpStatusCode> StatusWithBodyAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.BadRequest)
            Assert.Fail("400: " + await response.Content.ReadAsStringAsync());
        return response.StatusCode;
    }

    private static async Task<IReadOnlyList<string>> ErrorTargetsAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var targets = new List<string>();
        Collect(doc.RootElement, targets);
        return targets;

        static void Collect(JsonElement element, List<string> into)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.NameEquals("target") && property.Value.ValueKind == JsonValueKind.String)
                            into.Add(property.Value.GetString()!);
                        else
                            Collect(property.Value, into);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Collect(item, into);
                    break;
            }
        }
    }
}
