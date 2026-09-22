using System.Net;
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

// Top-level public types for the reason ODataTestServerRoutingTests documents: a nested controller
// is not IsPublic and MVC's ControllerFeatureProvider skips it.
//
// A chain four read types long, which is what it takes to ask the question: one hop is depth 1, so
// the first request that can exceed a ceiling of two needs three hops below the addressed set.
// Deliberately a separate fixture from ExpandAuthorizationEndToEndTests — that one's five
// assertions rest on EF's inference over its own two-type shape, and widening it to reach depth
// three would put this question and that one in a position to break each other.

public sealed class DepthA : IyuEntity { public Guid? BId { get; set; } }
public sealed class DepthB : IyuEntity { public Guid? CId { get; set; } }
public sealed class DepthC : IyuEntity { public Guid? DId { get; set; } }
public sealed class DepthD : IyuEntity { public string Marker { get; set; } = ""; }

public sealed class DepthAExt : IyuEntity
{
    public Guid? BId { get; set; }
    public DepthBExt? B { get; set; }
}

public sealed class DepthBExt : IyuEntity
{
    public Guid? CId { get; set; }
    public DepthCExt? C { get; set; }
}

public sealed class DepthCExt : IyuEntity
{
    public Guid? DId { get; set; }
    public DepthDExt? D { get; set; }
}

public sealed class DepthDExt : IyuEntity
{
    public string Marker { get; set; } = "";
}

public sealed class DepthContext(DbContextOptions<DepthContext> options) : IyuDbContext(options)
{
    public DbSet<DepthA> A => Set<DepthA>();
    public DbSet<DepthAExt> AExt => Set<DepthAExt>();
    public DbSet<DepthB> B => Set<DepthB>();
    public DbSet<DepthBExt> BExt => Set<DepthBExt>();
    public DbSet<DepthC> C => Set<DepthC>();
    public DbSet<DepthCExt> CExt => Set<DepthCExt>();
    public DbSet<DepthD> D => Set<DepthD>();
    public DbSet<DepthDExt> DExt => Set<DepthDExt>();
}

public sealed class DepthAsController(DepthContext ctx) : IyuODataController<DepthAExt, DepthA>(ctx);

/// <summary>
/// What limits how deep a <c>$expand</c> may go, measured rather than quoted.
/// </summary>
/// <remarks>
/// <para>
/// This framework sets no <c>MaxExpansionDepth</c>, so the ceiling is whatever
/// <c>EnableQueryAttribute</c> applies — a value owned by <c>Microsoft.AspNetCore.OData</c>, not by
/// this repository. That distinction is exactly why it is pinned here: a number this repository
/// does not set is a number that can change underneath it on a package upgrade, and the same
/// package has changed response behaviour silently across versions before. A README sentence about
/// how far an expand can reach is only worth publishing if something fails when it stops being
/// true.
/// </para>
/// <para>
/// <see cref="IyuExpandAuthorizationFilter"/> does not depend on this ceiling — it walks an expand
/// to the bottom and authorizes every set it reaches, at any depth. The two are pinned separately
/// on purpose: if the ceiling later rises, authorization coverage must not have been resting on it.
/// </para>
/// </remarks>
public class ExpandDepthEndToEndTests
{
    private const string Set = "DepthAs";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "depth-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddIyuMainServer<DepthContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(DepthAsController).Assembly);
                options.ODataModel.AddEntityPair<DepthAExt, DepthA>(Set);
                options.ODataModel.AddEntityPair<DepthBExt, DepthB>("DepthBs");
                options.ODataModel.AddEntityPair<DepthCExt, DepthC>("DepthCs");
                options.ODataModel.AddEntityPair<DepthDExt, DepthD>("DepthDs");
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> ExpandAsync(string expand)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/$data/{Set}?$expand={expand}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// One hop and two hops are accepted. This is the half a reader relies on when they model a
    /// pair of related read types and expect to fetch them together.
    /// </summary>
    [Theory]
    [InlineData("B")]
    [InlineData("B($expand=C)")]
    public async Task An_expand_within_the_ceiling_is_accepted(string expand)
    {
        var (status, _) = await ExpandAsync(expand);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// A third hop is refused, and refused <em>for being too deep</em> — the status alone does not
    /// establish that.
    /// </summary>
    /// <remarks>
    /// The measured ceiling is two. It is asserted on the response text rather than on `400` alone
    /// because the negative control below produces the same status for an entirely different
    /// reason: if the chain in this fixture were ever wired up wrong, every hop would fail to
    /// resolve and a status-only assertion would stay green while measuring nothing.
    /// <para>
    /// A failure here is not automatically a defect. This repository sets no
    /// <c>MaxExpansionDepth</c>, so the number belongs to the OData package and an upgrade may move
    /// it. The response says which limit it applied, so the fix is to read it, update this
    /// assertion, and update the README sentence that quotes the same number in the same commit.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_expand_past_the_ceiling_is_refused_for_being_too_deep()
    {
        var (status, body) = await ExpandAsync("B($expand=C($expand=D))");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("$expand path which is too deep", body, StringComparison.Ordinal);
        Assert.Contains("The maximum depth allowed is 2", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control that makes the assertion above mean something: an expand naming a
    /// property that does not exist is refused with the same `400`, and a different reason.
    /// </summary>
    /// <remarks>
    /// Without this, "depth three is refused" and "this fixture's navigation chain was never
    /// reachable in the first place" are indistinguishable from the outside — and the second one
    /// would make the depth assertion a test of nothing.
    /// </remarks>
    [Fact]
    public async Task An_expand_naming_an_unknown_property_is_refused_for_a_different_reason()
    {
        var (status, body) = await ExpandAsync("B($expand=Nonexistent)");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Could not find a property named 'Nonexistent'", body, StringComparison.Ordinal);
        Assert.DoesNotContain("too deep", body, StringComparison.Ordinal);
    }
}
