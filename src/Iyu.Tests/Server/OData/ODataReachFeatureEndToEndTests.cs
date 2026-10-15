using System.Net;
using Iyu.Core.Attributes;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the reason ODataTestServerRoutingTests documents: a nested controller
// is not IsPublic and MVC's ControllerFeatureProvider skips it. Read types carry their navigations by
// hand, as in ExpandAuthorizationEndToEndTests.

public sealed class ReachPerson : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class ReachPersonExt : IyuEntity
{
    [Searchable] public string Name { get; set; } = "";
}

public sealed class ReachSite : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class ReachSiteExt : IyuEntity
{
    [Searchable] public string Name { get; set; } = "";
}

public sealed class ReachTeam : IyuEntity
{
    public string Name { get; set; } = "";
    public Guid? LeadId { get; set; }
    public Guid? SiteId { get; set; }
}

public sealed class ReachTeamExt : IyuEntity
{
    [Searchable] public string Name { get; set; } = "";
    public Guid? LeadId { get; set; }
    public Guid? SiteId { get; set; }
    [Searchable] public ReachPersonExt? Lead { get; set; }
    [Searchable] public ReachSiteExt? Site { get; set; }
    public ICollection<ReachTaskExt> Tasks { get; set; } = [];
}

public sealed class ReachTask : IyuEntity
{
    public string Title { get; set; } = "";
    public Guid? TeamId { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? SiteId { get; set; }
}

public sealed class ReachTaskExt : IyuEntity
{
    [Searchable] public string Title { get; set; } = "";
    public Guid? TeamId { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? SiteId { get; set; }
    public ReachTeamExt? Team { get; set; }
    [Searchable] public ReachPersonExt? Assignee { get; set; }
    [Searchable] public ReachSiteExt? Site { get; set; }
}

public sealed class ReachContext(DbContextOptions<ReachContext> options) : IyuDbContext(options)
{
    public DbSet<ReachPerson> People => Set<ReachPerson>();
    public DbSet<ReachPersonExt> PeopleExt => Set<ReachPersonExt>();
    public DbSet<ReachSite> Sites => Set<ReachSite>();
    public DbSet<ReachSiteExt> SitesExt => Set<ReachSiteExt>();
    public DbSet<ReachTeam> Teams => Set<ReachTeam>();
    public DbSet<ReachTeamExt> TeamsExt => Set<ReachTeamExt>();
    public DbSet<ReachTask> Tasks => Set<ReachTask>();
    public DbSet<ReachTaskExt> TasksExt => Set<ReachTaskExt>();
}

public sealed class ReachPeopleController(ReachContext ctx) : IyuODataController<ReachPersonExt, ReachPerson>(ctx);
public sealed class ReachSitesController(ReachContext ctx) : IyuODataController<ReachSiteExt, ReachSite>(ctx);
public sealed class ReachTeamsController(ReachContext ctx) : IyuODataController<ReachTeamExt, ReachTeam>(ctx);
public sealed class ReachTasksController(ReachContext ctx) : IyuODataController<ReachTaskExt, ReachTask>(ctx);

/// <summary>
/// <see cref="IIyuODataReachFeature"/> through the real pipeline: what a host that records reads is handed, for an
/// allowed request, a refused one, one the check cannot account for, and a search into a reference — and on a host
/// with no read policy, with and without asking for it.
/// </summary>
public class ODataReachFeatureEndToEndTests
{
    private const string PeopleRead = "people.read";
    private const string ReachHeader = "X-Reach";

    private static async Task<WebApplication> StartAsync(bool protectPeople = true, bool computeReach = false)
    {
        var dbName = "reach-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(PeopleRead, p => p.RequireClaim("perm", PeopleRead)));
        builder.Services.AddIyuMainServer<ReachContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ReachTeamsController).Assembly);
                options.ODataModel.AddEntityPair<ReachPersonExt, ReachPerson>("ReachPeople");
                options.ODataModel.AddEntityPair<ReachSiteExt, ReachSite>("ReachSites");
                options.ODataModel.AddEntityPair<ReachTeamExt, ReachTeam>("ReachTeams");
                options.ODataModel.AddEntityPair<ReachTaskExt, ReachTask>("ReachTasks");
                if (protectPeople) options.Authorize<ReachPersonExt>(read: PeopleRead);
                options.ComputeODataReach = computeReach;
            });

        var app = builder.Build();
        // What a host that records reads does: read the feature once the request is answered. Surfaced as a header
        // so the tests see exactly what such a host would have been handed — absent when the feature is.
        app.Use(async (http, next) =>
        {
            http.Response.OnStarting(() =>
            {
                if (http.Features.Get<IIyuODataReachFeature>() is { } reach)
                    http.Response.Headers[ReachHeader] = reach.EntitySet + "|" + string.Join(",", reach.ReachedEntitySets);
                return Task.CompletedTask;
            });
            await next();
        });
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReachContext>();
        var personId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        ctx.PeopleExt.Add(new ReachPersonExt { Id = personId, Name = "Kim" });
        ctx.SitesExt.Add(new ReachSiteExt { Id = siteId, Name = "North" });
        ctx.TeamsExt.Add(new ReachTeamExt { Id = teamId, Name = "Ops", LeadId = personId, SiteId = siteId });
        ctx.TasksExt.Add(new ReachTaskExt
        {
            Id = Guid.NewGuid(), Title = "Inspect", TeamId = teamId, AssigneeId = personId, SiteId = siteId,
        });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string? Reach, string? Excluded)> GetAsync(
        WebApplication app, string pathAndQuery, string? perm)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/$data/" + pathAndQuery);
        if (perm is not null) req.Headers.Add("X-Test-Perm", perm);
        using var resp = await app.GetTestServer().CreateClient().SendAsync(req);
        var reach = resp.Headers.TryGetValues(ReachHeader, out var r) ? string.Join(";", r) : null;
        var excluded = resp.Headers.TryGetValues(IyuStringSearchBinder.ExcludedHeader, out var e) ? string.Join(",", e) : null;
        return (resp.StatusCode, reach, excluded);
    }

    [Fact]
    public async Task An_allowed_request_names_the_set_it_addressed_and_every_set_its_navigations_read()
    {
        var app = await StartAsync();
        try
        {
            // Expanded into, and used only to filter — both are reads.
            var (status, reach, _) = await GetAsync(
                app, "ReachTeams?$expand=Lead&$filter=Site/Name eq 'North'", PeopleRead);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTeams|ReachPeople,ReachSites", reach);

            // Two navigations deep.
            (status, reach, _) = await GetAsync(app, "ReachTasks?$expand=Team($expand=Lead)", PeopleRead);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTasks|ReachPeople,ReachTeams", reach);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_request_that_reads_only_the_set_it_addressed_carries_an_empty_reach()
    {
        var app = await StartAsync();
        try
        {
            var (status, reach, _) = await GetAsync(app, "ReachTeams", perm: "anyone");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTeams|", reach);

            (status, reach, _) = await GetAsync(app, "ReachTeams?$filter=Name eq 'Ops'&$orderby=Name", perm: "anyone");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTeams|", reach);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A refused attempt is still a record of what was tried — the feature is set before the policy is.</summary>
    [Fact]
    public async Task A_refused_request_carries_the_sets_it_tried_to_reach()
    {
        var app = await StartAsync();
        try
        {
            var (status, reach, _) = await GetAsync(app, "ReachTeams?$filter=Lead/Name eq 'Kim'", perm: "anyone");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("ReachTeams|ReachPeople", reach);

            (status, reach, _) = await GetAsync(app, "ReachTeams?$expand=Lead", perm: null);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("ReachTeams|ReachPeople", reach);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// Absent, not empty, when the framework could not determine the reach: an empty list would say «read nothing
    /// else», which is not known.
    /// </summary>
    [Fact]
    public async Task A_request_refused_for_a_query_the_check_cannot_account_for_carries_no_reach()
    {
        var app = await StartAsync();
        try
        {
            var (status, reach, _) = await GetAsync(app, "ReachTeams?$expand=Lead&expand=Site", PeopleRead);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Null(reach);

            (status, reach, _) = await GetAsync(app, "ReachTeams?$filter=Nope/Name eq 'x'", PeopleRead);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Null(reach);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// A search matches the text of a searchable reference, which reads the referenced set's values as surely as a
    /// filter on them does. The reach names exactly the references the search follows — the protected one only for a
    /// caller who may read it, the same split the binder reports in its excluded header.
    /// </summary>
    [Fact]
    public async Task A_search_reaches_the_searchable_references_it_follows_for_this_caller()
    {
        var app = await StartAsync();
        try
        {
            var (status, reach, excluded) = await GetAsync(app, "ReachTeams?$search=kim", PeopleRead);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTeams|ReachPeople,ReachSites", reach);
            Assert.Null(excluded);

            (status, reach, excluded) = await GetAsync(app, "ReachTeams?search=kim", perm: "anyone");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTeams|ReachSites", reach);
            Assert.Equal("Lead", excluded);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>
    /// A search nested in an expand binds the expanded type, for which nothing is listed as allowed — its protected
    /// references are left out even for a caller who could read them, and the reach says the same.
    /// </summary>
    [Fact]
    public async Task A_search_nested_in_an_expand_reaches_only_the_unprotected_references_of_the_expanded_type()
    {
        var app = await StartAsync();
        try
        {
            var (status, reach, excluded) = await GetAsync(app, "ReachTeams?$expand=Tasks($search=kim)", PeopleRead);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTeams|ReachSites,ReachTasks", reach);
            Assert.Equal("Assignee", excluded);

            // Under a single-valued navigation the query pipeline does not apply a nested search (measured: the
            // expanded row is returned whatever the text). The reach counts it anyway — a record that may name a
            // reference the search did not follow, never one that leaves out a reference it did, whichever way the
            // pipeline goes.
            (status, reach, _) = await GetAsync(app, "ReachTasks?$expand=Team($search=kim)", PeopleRead);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTasks|ReachSites,ReachTeams", reach);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task On_a_host_with_no_read_policy_the_reach_is_published_only_when_asked_for()
    {
        var unasked = await StartAsync(protectPeople: false);
        try
        {
            var (status, reach, _) = await GetAsync(unasked, "ReachTeams?$expand=Lead", perm: null);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Null(reach);
        }
        finally { await unasked.DisposeAsync(); }

        var asked = await StartAsync(protectPeople: false, computeReach: true);
        try
        {
            var (status, reach, _) = await GetAsync(asked, "ReachTeams?$expand=Lead", perm: null);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ReachTeams|ReachPeople", reach);

            // The check's refusals come with it: a query it cannot account for is not run.
            (status, reach, _) = await GetAsync(asked, "ReachTeams?$expand=Lead&expand=Site", perm: null);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Null(reach);
        }
        finally { await asked.DisposeAsync(); }
    }
}
