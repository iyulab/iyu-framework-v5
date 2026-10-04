using System.Net;
using System.Net.Http.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

public sealed class SurfaceRegion : IyuEntity
{
    public string Label { get; set; } = "";
}

public sealed class SurfaceRegionExt : IyuEntity
{
    public string Label { get; set; } = "";
}

public sealed class SurfaceSite : IyuEntity
{
    public string Title { get; set; } = "";
    public Guid? RegionId { get; set; }
}

public sealed class SurfaceSiteExt : IyuEntity
{
    public string Title { get; set; } = "";
    public Guid? RegionId { get; set; }
    public SurfaceRegionExt? Region { get; set; }
}

public sealed class SurfaceContext(DbContextOptions<SurfaceContext> options) : IyuDbContext(options)
{
    public DbSet<SurfaceSite> Sites => Set<SurfaceSite>();
    public DbSet<SurfaceSiteExt> SitesExt => Set<SurfaceSiteExt>();
    public DbSet<SurfaceRegion> Regions => Set<SurfaceRegion>();
    public DbSet<SurfaceRegionExt> RegionsExt => Set<SurfaceRegionExt>();
}

/// <summary>
/// A host registers one surface only. A registration may name its entities on GraphQL alone (no
/// OData set at all) or on OData alone, and the host must start and serve exactly that surface —
/// a code generator that lets a deployment pick its surfaces relies on both shapes.
/// </summary>
public class SingleSurfaceHostTests
{
    private const string RegionLabel = "north-region";

    private static async Task<WebApplication> StartAsync(Action<IyuMainServerOptions> register)
    {
        var dbName = "surface-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<SurfaceContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: register);

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<SurfaceContext>();
        var regionId = Guid.NewGuid();
        ctx.RegionsExt.Add(new SurfaceRegionExt { Id = regionId, Label = RegionLabel });
        ctx.SitesExt.Add(new SurfaceSiteExt { Id = Guid.NewGuid(), Title = "a site", RegionId = regionId });
        await ctx.SaveChangesAsync();
        return app;
    }

    [Fact]
    public async Task A_GraphQL_only_host_starts_and_resolves_a_navigation_over_HTTP()
    {
        var app = await StartAsync(options =>
        {
            options.GraphQL.AddEntityPair<SurfaceSiteExt, SurfaceSite>("surfaceSites", "surfaceSite");
            options.GraphQL.AddEntityPair<SurfaceRegionExt, SurfaceRegion>("surfaceRegions", "surfaceRegion");
        });
        try
        {
            var client = app.GetTestServer().CreateClient();
            using var resp = await client.PostAsJsonAsync("/graphql",
                new { query = "{ surfaceSites { nodes { title region { label } } } }" });
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.DoesNotContain("\"errors\"", body, StringComparison.Ordinal);
            Assert.Contains(RegionLabel, body, StringComparison.Ordinal);

            // No OData set was registered, so the data API describes none.
            var metadata = await client.GetAsync("/$data/$metadata");
            var metadataBody = await metadata.Content.ReadAsStringAsync();
            Assert.DoesNotContain(nameof(SurfaceSiteExt), metadataBody, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task An_OData_only_host_maps_no_GraphQL_endpoint()
    {
        var app = await StartAsync(options =>
        {
            options.ODataModel.AddEntityPair<SurfaceSiteExt, SurfaceSite>("SurfaceSites");
            options.ODataModel.AddEntityPair<SurfaceRegionExt, SurfaceRegion>("SurfaceRegions");
        });
        try
        {
            var client = app.GetTestServer().CreateClient();
            using var resp = await client.PostAsJsonAsync("/graphql",
                new { query = "{ __typename }" });

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }
}
