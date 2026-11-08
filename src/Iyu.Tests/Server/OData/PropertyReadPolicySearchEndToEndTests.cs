using System.Net;
using System.Text.Json;
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

/// <summary>
/// <c>$search</c> follows a searchable reference one step and matches the referenced row's text. A property read policy on
/// the referenced row's property therefore has to hold there as well: the search is a way to test the value, and a caller
/// who may not read it must not get an answer that says whether a guess is right. Here <c>ReachPeople.Name</c> needs
/// <c>names.read</c> while the people set itself is open, and a team's <c>Lead</c> is a searchable reference to a person.
/// </summary>
public class PropertyReadPolicySearchEndToEndTests
{
    private const string NamesRead = "names.read";
    private const string LeadName = "Kim";

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(NamesRead, p => p.RequireClaim("perm", NamesRead)));
        var dbName = "propsearch-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<ReachContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ReachTeamsController).Assembly);
                options.ODataModel.AddEntityPair<ReachPersonExt, ReachPerson>("ReachPeople");
                options.ODataModel.AddEntityPair<ReachSiteExt, ReachSite>("ReachSites");
                options.ODataModel.AddEntityPair<ReachTeamExt, ReachTeam>("ReachTeams");
                options.ODataModel.AddEntityPair<ReachTaskExt, ReachTask>("ReachTasks");
                options.AuthorizeProperty<ReachPersonExt>(x => x.Name, NamesRead);
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReachContext>();
        var personId = Guid.NewGuid();
        ctx.PeopleExt.Add(new ReachPersonExt { Id = personId, Name = LeadName });
        ctx.TeamsExt.Add(new ReachTeamExt { Id = Guid.NewGuid(), Name = "Ops", LeadId = personId });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, int Rows, string Body)> SearchAsync(WebApplication app, string search, params string[] perms)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/$data/ReachTeams?$search={search}&$select=Id");
        foreach (var perm in perms) request.Headers.Add("X-Test-Perm", perm);
        using var response = await app.GetTestServer().CreateClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var rows = response.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(body).RootElement.GetProperty("value").GetArrayLength() : -1;
        return (response.StatusCode, rows, body);
    }

    /// <summary>
    /// The team is named "Ops"; only its lead is named "Kim". A search for "Kim" that finds the team has tested the lead's
    /// name through the reference — which the caller may not read.
    /// </summary>
    [Fact]
    public async Task A_search_does_not_test_a_referenced_property_the_caller_may_not_read()
    {
        var app = await StartAsync();
        try
        {
            var (status, rows, body) = await SearchAsync(app, LeadName, "something.else");
            Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
            Assert.Equal(0, rows);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_search_tests_the_referenced_property_for_a_caller_who_may_read_it()
    {
        var app = await StartAsync();
        try
        {
            var (status, rows, body) = await SearchAsync(app, LeadName, NamesRead);
            Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
            Assert.Equal(1, rows);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>Left out, not refused: the team's own text is still searched for the caller who may not read the lead's name.</summary>
    [Fact]
    public async Task The_callers_own_text_is_still_searched_when_the_reference_is_left_out()
    {
        var app = await StartAsync();
        try
        {
            var (status, rows, body) = await SearchAsync(app, "Ops", "something.else");
            Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
            Assert.Equal(1, rows);
        }
        finally { await app.DisposeAsync(); }
    }
}
