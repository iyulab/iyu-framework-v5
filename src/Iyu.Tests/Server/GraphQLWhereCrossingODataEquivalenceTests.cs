using System.Net.Http.Json;
using System.Text.Json;
using Iyu.MainServer;
using Iyu.Tests.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server;

/// <summary>
/// A condition that crosses a reference or a collection answers the same way on both surfaces: <c>where: { lead: { name: … } }</c>
/// and <c>$filter=Lead/Name eq …</c> return the same teams, <c>some</c> / <c>all</c> / <c>none</c> / <c>any</c> match OData's
/// <c>any</c> / <c>all</c>, and an ordering through a reference puts the rows in the same order. The teams include one with no
/// lead and one with no tasks, because the empty cases are where two surfaces most easily part.
/// </summary>
public sealed class GraphQLWhereCrossingODataEquivalenceTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "where-crossing-equivalence-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddIyuMainServer<ReachContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ReachTeamsController).Assembly);
                options.ODataModel.AddEntityPair<ReachPersonExt, ReachPerson>("ReachPeople");
                options.ODataModel.AddEntityPair<ReachSiteExt, ReachSite>("ReachSites");
                options.ODataModel.AddEntityPair<ReachTeamExt, ReachTeam>("ReachTeams");
                options.ODataModel.AddEntityPair<ReachTaskExt, ReachTask>("ReachTasks");
                options.GraphQL.AddEntityPair<ReachPersonExt, ReachPerson>("reachPeople", "reachPerson");
                options.GraphQL.AddEntityPair<ReachSiteExt, ReachSite>("reachSites", "reachSite");
                options.GraphQL.AddEntityPair<ReachTeamExt, ReachTeam>("reachTeams", "reachTeam");
                options.GraphQL.AddEntityPair<ReachTaskExt, ReachTask>("reachTasks", "reachTask");
                options.GraphQL.WhereAndOrder = true;
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReachContext>();
        var kim = new ReachPersonExt { Id = Guid.NewGuid(), Name = "Kim" };
        var lee = new ReachPersonExt { Id = Guid.NewGuid(), Name = "Lee" };
        ctx.PeopleExt.AddRange(kim, lee);
        var alpha = new ReachTeamExt { Id = Guid.NewGuid(), Name = "alpha", LeadId = kim.Id };
        var bravo = new ReachTeamExt { Id = Guid.NewGuid(), Name = "bravo", LeadId = lee.Id };
        var charlie = new ReachTeamExt { Id = Guid.NewGuid(), Name = "charlie" };   // no lead, no tasks
        ctx.TeamsExt.AddRange(alpha, bravo, charlie);
        ctx.TasksExt.AddRange(
            new ReachTaskExt { Id = Guid.NewGuid(), Title = "t1", TeamId = alpha.Id },
            new ReachTaskExt { Id = Guid.NewGuid(), Title = "t2", TeamId = alpha.Id },
            new ReachTaskExt { Id = Guid.NewGuid(), Title = "t3", TeamId = bravo.Id },
            new ReachTaskExt { Id = Guid.NewGuid(), Title = "x9", TeamId = bravo.Id });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static async Task<string[]> ODataAsync(HttpClient client, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/$data/ReachTeams?$select=Name&" + query);
        request.Headers.Add("X-Test-Perm", "anyone");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("value").EnumerateArray()
            .Select(r => r.GetProperty("Name").GetString()!).ToArray();
    }

    private static async Task<string[]> GraphQLAsync(HttpClient client, string arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new { query = "{ reachTeams(" + arguments + ") { nodes { name } } }" }),
        };
        request.Headers.Add("X-Test-Perm", "anyone");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"errors\"", text, StringComparison.Ordinal);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("reachTeams").GetProperty("nodes")
            .EnumerateArray().Select(n => n.GetProperty("name").GetString()!).ToArray();
    }

    public static TheoryData<string, string, string> Conditions => new()
    {
        // through a single reference
        { "$filter=Lead/Name eq 'Kim'", "where: { lead: { name: { eq: \"Kim\" } } }", "alpha" },
        { "$filter=Lead/Name ne 'Kim'", "where: { lead: { name: { neq: \"Kim\" } } }", "bravo,charlie" },
        { "$filter=Lead/Name eq 'nobody'", "where: { lead: { name: { eq: \"nobody\" } } }", "" },
        { "$filter=startswith(Lead/Name,'K')", "where: { lead: { name: { startsWith: \"K\" } } }", "alpha" },
        // through a collection
        { "$filter=Tasks/any(t: t/Title eq 't1')", "where: { tasks: { some: { title: { eq: \"t1\" } } } }", "alpha" },
        { "$filter=not Tasks/any(t: t/Title eq 't1')", "where: { tasks: { none: { title: { eq: \"t1\" } } } }", "bravo,charlie" },
        { "$filter=Tasks/all(t: startswith(t/Title,'t'))", "where: { tasks: { all: { title: { startsWith: \"t\" } } } }", "alpha,charlie" },
        { "$filter=Tasks/any()", "where: { tasks: { any: true } }", "alpha,bravo" },
        { "$filter=not Tasks/any()", "where: { tasks: { any: false } }", "charlie" },
        // a crossing combined with a condition on the row itself
        { "$filter=Name eq 'alpha' or Lead/Name eq 'Lee'", "where: { or: [{ name: { eq: \"alpha\" } }, { lead: { name: { eq: \"Lee\" } } }] }", "alpha,bravo" },
    };

    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task A_condition_across_a_reference_or_collection_returns_the_same_rows_on_both_surfaces(string odata, string graphql, string expected)
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();

            var viaOData = await ODataAsync(client, odata);
            var viaGraphQL = await GraphQLAsync(client, graphql);

            var want = expected.Length == 0 ? [] : expected.Split(',');
            Assert.Equal(want, viaOData.Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(want, viaGraphQL.Order(StringComparer.Ordinal).ToArray());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task An_ordering_through_a_reference_puts_the_rows_in_the_same_order_on_both_surfaces()
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();

            var asc = (await ODataAsync(client, "$orderby=Lead/Name asc,Name asc"), await GraphQLAsync(client, "order: [{ lead: { name: ASC } }, { name: ASC }]"));
            var desc = (await ODataAsync(client, "$orderby=Lead/Name desc,Name asc"), await GraphQLAsync(client, "order: [{ lead: { name: DESC } }, { name: ASC }]"));

            // Where a row with no lead sorts is the database's rule (the in-memory provider here puts it first ascending, last
            // descending); both surfaces agree because they ask the same database.
            Assert.Equal(new[] { "charlie", "alpha", "bravo" }, asc.Item1);
            Assert.Equal(new[] { "charlie", "alpha", "bravo" }, asc.Item2);
            Assert.Equal(new[] { "bravo", "alpha", "charlie" }, desc.Item1);
            Assert.Equal(new[] { "bravo", "alpha", "charlie" }, desc.Item2);
        }
        finally { await app.DisposeAsync(); }
    }
}
