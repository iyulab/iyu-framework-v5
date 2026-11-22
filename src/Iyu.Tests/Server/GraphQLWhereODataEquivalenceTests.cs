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
/// The same set served on both surfaces answers the same question the same way: <c>where</c> on a GraphQL query field and
/// <c>$filter</c> on the OData set return the same rows, and <c>order</c> and <c>$orderby</c> put them in the same order. The
/// point of the feature is that a model is readable with the same ability wherever it is served.
/// </summary>
public sealed class GraphQLWhereODataEquivalenceTests
{
    private static readonly string[] Names = ["ann", "anna", "bob", "carl", "dan", "Eve"];

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "where-equivalence-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddIyuMainServer<PolicyWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(PolicyWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<PolicyWidgetExt, PolicyWidget>("PolicyWidgets");
                options.GraphQL.AddEntityPair<PolicyWidgetExt, PolicyWidget>("policyWidgets", "policyWidget");
                options.GraphQL.WhereAndOrder = true;
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PolicyWidgetContext>();
        foreach (var name in Names) ctx.WidgetsExt.Add(new PolicyWidgetExt { Id = Guid.NewGuid(), Name = name });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static async Task<string[]> ODataAsync(HttpClient client, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/$data/PolicyWidgets?$select=Name&" + query);
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
            Content = JsonContent.Create(new { query = "{ policyWidgets(" + arguments + ") { nodes { name } } }" }),
        };
        request.Headers.Add("X-Test-Perm", "anyone");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"errors\"", text, StringComparison.Ordinal);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("policyWidgets").GetProperty("nodes")
            .EnumerateArray().Select(n => n.GetProperty("name").GetString()!).ToArray();
    }

    public static TheoryData<string, string> Conditions => new()
    {
        { "$filter=Name eq 'bob'", "where: { name: { eq: \"bob\" } }" },
        { "$filter=Name ne 'bob'", "where: { name: { neq: \"bob\" } }" },
        { "$filter=contains(Name,'an')", "where: { name: { contains: \"an\" } }" },
        { "$filter=startswith(Name,'a')", "where: { name: { startsWith: \"a\" } }" },
        { "$filter=endswith(Name,'n')", "where: { name: { endsWith: \"n\" } }" },
        { "$filter=Name in ('ann','dan','nobody')", "where: { name: { in: [\"ann\", \"dan\", \"nobody\"] } }" },
        { "$filter=Name eq 'ann' or Name eq 'carl'", "where: { or: [{ name: { eq: \"ann\" } }, { name: { eq: \"carl\" } }] }" },
        { "$filter=Name eq 'zzz'", "where: { name: { eq: \"zzz\" } }" },
    };

    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task A_condition_returns_the_same_rows_on_both_surfaces(string odata, string graphql)
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();

            var viaOData = await ODataAsync(client, odata);
            var viaGraphQL = await GraphQLAsync(client, graphql);

            Assert.Equal(viaOData.Order(StringComparer.Ordinal).ToArray(), viaGraphQL.Order(StringComparer.Ordinal).ToArray());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Text_is_not_compared_by_range_on_GraphQL_where_OData_can_a_known_difference()
    {
        // HotChocolate's text filter offers equality, containment, prefix, suffix and membership; OData's $filter also compares
        // text by range. The difference is stated rather than hidden: the schema simply has no such operation, and a request
        // that writes one is refused by validation without a query having run.
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();

            var viaOData = await ODataAsync(client, "$filter=Name gt 'b'");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
            {
                Content = JsonContent.Create(new { query = "{ policyWidgets(where: { name: { gt: \"b\" } }) { nodes { name } } }" }),
            };
            request.Headers.Add("X-Test-Perm", "anyone");
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(viaOData);
            Assert.Contains("\"errors\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"nodes\"", text, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task An_ordering_puts_the_rows_in_the_same_order_on_both_surfaces()
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();

            var asc = (await ODataAsync(client, "$orderby=Name asc"), await GraphQLAsync(client, "order: { name: ASC }"));
            var desc = (await ODataAsync(client, "$orderby=Name desc"), await GraphQLAsync(client, "order: { name: DESC }"));

            Assert.Equal(asc.Item1, asc.Item2);
            Assert.Equal(desc.Item1, desc.Item2);
        }
        finally { await app.DisposeAsync(); }
    }
}
