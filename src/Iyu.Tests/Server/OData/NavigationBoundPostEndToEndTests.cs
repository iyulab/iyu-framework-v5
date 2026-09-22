using System.Net;
using System.Net.Http.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the reason ODataTestServerRoutingTests documents: a nested controller
// is not IsPublic and MVC's ControllerFeatureProvider skips it.
//
// A pair that shares its key: the dependent's primary key IS its foreign key to the principal.
// That is the shape a model declares when one type carries optional extra facts about another
// rather than a collection of them, and it is why the pair cannot be created by posting to the
// dependent's own set and hoping a key is invented — the key is not the dependent's to choose.

public sealed class SharedKeyAsset : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class SharedKeyMaintenance : IyuEntity
{
    public string Interval { get; set; } = "";
}

public sealed class SharedKeyAssetExt : IyuEntity
{
    public string Name { get; set; } = "";

    public SharedKeyMaintenanceExt? MaintenanceProfile { get; set; }
}

public sealed class SharedKeyMaintenanceExt : IyuEntity
{
    public string Interval { get; set; } = "";
}

public sealed class SharedKeyContext(DbContextOptions<SharedKeyContext> options) : IyuDbContext(options)
{
    public DbSet<SharedKeyAsset> Assets => Set<SharedKeyAsset>();
    public DbSet<SharedKeyAssetExt> AssetsExt => Set<SharedKeyAssetExt>();
    public DbSet<SharedKeyMaintenance> Maintenances => Set<SharedKeyMaintenance>();
    public DbSet<SharedKeyMaintenanceExt> MaintenancesExt => Set<SharedKeyMaintenanceExt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Stated rather than inferred. Convention does find a one-to-one between two types related
        // by a nullable reference — ViewBackedOneToOneModelTests pins that — but it cannot find
        // one whose foreign key is the dependent's own primary key, because there is no
        // `<Principal>Id` property to recognise. The shape under measurement here is exactly that
        // one, so it is configured explicitly.
        modelBuilder.Entity<SharedKeyAssetExt>()
            .HasOne(a => a.MaintenanceProfile)
            .WithOne()
            .HasForeignKey<SharedKeyMaintenanceExt>(m => m.Id);
    }
}

public sealed class AssetsController(SharedKeyContext ctx)
    : IyuODataController<SharedKeyAssetExt, SharedKeyAsset>(ctx);

public sealed class MaintenanceProfilesController(SharedKeyContext ctx)
    : IyuODataController<SharedKeyMaintenanceExt, SharedKeyMaintenance>(ctx);

/// <summary>
/// Whether a POST addressed through a navigation property — <c>POST /$data/Assets({key})/MaintenanceProfile</c>
/// — reaches anything on this framework's generic OData controller.
/// </summary>
/// <remarks>
/// <para>
/// The question is a routing one, not a persistence one: OData defines the address, and
/// <c>Microsoft.AspNetCore.OData</c> binds it to an action by convention. This fixture measures
/// which of those conventions this repository's controller actually satisfies, so a decision about
/// the write path for a shared-key pair rests on what the stack does rather than on what the
/// protocol permits.
/// </para>
/// <para>
/// A failure here is not automatically a defect in this repository — the conventions belong to the
/// OData package. What the assertions pin is the <em>consequence</em>: which address a consumer can
/// use today to create the dependent half of a shared-key pair.
/// </para>
/// </remarks>
public class NavigationBoundPostEndToEndTests
{
    private const string Principals = "Assets";
    private const string Dependents = "MaintenanceProfiles";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "sharedkey-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddIyuMainServer<SharedKeyContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(AssetsController).Assembly);
                options.ODataModel.AddEntityPair<SharedKeyAssetExt, SharedKeyAsset>(Principals);
                options.ODataModel.AddEntityPair<SharedKeyMaintenanceExt, SharedKeyMaintenance>(Dependents);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// Seeds the principal on <em>both</em> halves of the pair.
    /// </summary>
    /// <remarks>
    /// The read set is the one every GET is served from, and in this in-memory fixture it is a
    /// separate store rather than a view over the write table. Seeding only the write side leaves
    /// a key-addressed GET answering 404 — which would make the negative control below agree with
    /// the measurement for the wrong reason, and the pair of them would establish nothing.
    /// </remarks>
    private static async Task<Guid> SeedPrincipalAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<SharedKeyContext>();
        var id = Guid.NewGuid();
        ctx.Assets.Add(new SharedKeyAsset { Id = id, Name = "pump" });
        ctx.AssetsExt.Add(new SharedKeyAssetExt { Id = id, Name = "pump" });
        await ctx.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// The measurement this task exists for: posting through the navigation property.
    /// </summary>
    [Fact]
    public async Task A_post_addressed_through_a_navigation_property_is_not_routed()
    {
        await using var app = await StartAsync();
        var key = await SeedPrincipalAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Principals}({key})/MaintenanceProfile",
            new { Interval = "P30D" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The negative control that makes the measurement above mean something: the same navigation
    /// property IS addressable for reading, so a 404 on the POST is about the write convention and
    /// not about a fixture whose navigation was never in the model at all.
    /// </summary>
    [Fact]
    public async Task The_same_navigation_property_is_addressable_for_reading()
    {
        await using var app = await StartAsync();
        var key = await SeedPrincipalAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            $"/$data/{Principals}({key})?$expand=MaintenanceProfile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// No route template on this host binds a POST underneath an entity key — which is the reason
    /// for the 404 above, stated where a reader can check it rather than inferred from a status.
    /// </summary>
    /// <remarks>
    /// Key-bound templates do exist, for the methods that address a single entity. Asserting that
    /// first is what stops the second assertion from passing vacuously: "no POST is bound beneath a
    /// key" and "this enumeration never sees a key-bound template at all" would otherwise look the
    /// same from here, and only the first of them says anything.
    /// </remarks>
    [Fact]
    public async Task No_endpoint_binds_a_post_beneath_an_entity_key()
    {
        await using var app = await StartAsync();

        var templates = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                Pattern: e.RoutePattern.RawText ?? ""))
            .ToList();

        var keyBound = templates
            .Where(t => t.Pattern.Contains("{key}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(keyBound);
        Assert.DoesNotContain(keyBound, t => t.Methods.Contains(HttpMethods.Post));
    }

    /// <summary>
    /// The address that does work today: the dependent's own entity set. Recorded here because it
    /// is the alternative any decision about the write path has to be weighed against.
    /// </summary>
    [Fact]
    public async Task A_post_to_the_dependents_own_set_is_routed()
    {
        await using var app = await StartAsync();
        var key = await SeedPrincipalAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Dependents}",
            new { Id = key, Interval = "P30D" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// What that address does when the body carries no key: it invents one.
    /// </summary>
    /// <remarks>
    /// For an independent type this is the right behaviour and the reason the generic POST works at
    /// all. For a type whose key is its foreign key it is not — the key is the principal's, so a
    /// generated one produces a row that belongs to nothing and can never be reached through the
    /// navigation it was supposed to fill. Measured rather than assumed, because it decides whether
    /// the write contract needs a refusal or merely documentation.
    /// </remarks>
    [Fact]
    public async Task A_post_to_the_dependents_own_set_invents_a_key_when_the_body_omits_one()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Dependents}",
            new { Interval = "P30D" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<SharedKeyContext>();
        var written = Assert.Single(ctx.Maintenances);
        Assert.NotEqual(Guid.Empty, written.Id);
    }

    /// <summary>
    /// And what it does when the key names a principal that does not exist: it accepts it.
    /// </summary>
    /// <remarks>
    /// The write set here is a table of its own, so nothing on this path consults the principal.
    /// That is the second half of the same gap: today neither an absent key nor an unmatched one is
    /// refused, so a shared-key pair can be half-created in two different ways without a single
    /// error being raised.
    /// </remarks>
    [Fact]
    public async Task A_post_naming_a_principal_that_does_not_exist_is_accepted()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Dependents}",
            new { Id = Guid.NewGuid(), Interval = "P30D" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
