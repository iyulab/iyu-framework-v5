using System.Net;
using System.Net.Http.Json;
using Iyu.MainServer;
using Iyu.Server.GraphQL;
using Iyu.Tests.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.GraphQL;

/// <summary>
/// A model that is only read, served on the GraphQL surface (<see cref="IyuGraphQLSchemaBuilder.AddReadOnlySet{TRead}"/>):
/// a query field like a pair's, no write type behind it, nothing for a mutation to attach to, and the entity's read
/// policy on it. The model is the one the OData read-only tests use (<see cref="FreightSummary"/>) — a key and what a
/// view would project.
/// </summary>
public class GraphQLReadOnlySetTests
{
    private const string Field = "freightSummaries";
    private const string ReadPolicy = "freight.read";

    private static async Task<WebApplication> StartAsync(bool withReadPolicy = false, bool onBothSurfaces = false)
    {
        var dbName = "gql-freight-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (withReadPolicy)
        {
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
            builder.Services.AddAuthorization(opts => opts.AddPolicy(ReadPolicy, p => p.RequireClaim("perm", ReadPolicy)));
        }
        builder.Services.AddIyuMainServer<FreightSummaryContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.GraphQL.AddReadOnlySet<FreightSummary>(Field);
                if (onBothSurfaces)
                {
                    options.ControllerAssemblies.Add(typeof(FreightSummariesController).Assembly);
                    options.ODataModel.AddReadOnlySet<FreightSummary>("FreightSummaries");
                }
                if (withReadPolicy) options.Authorize<FreightSummary>(read: ReadPolicy);
            });

        var app = builder.Build();
        if (withReadPolicy) app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        // Rows arrive the way a view's would: something else wrote the data this model reads.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FreightSummaryContext>();
        db.Summaries.AddRange(
            new FreightSummary { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Carrier = "north", Parcels = 3 },
            new FreightSummary { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), Carrier = "south", Parcels = 12 });
        await db.SaveChangesAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> QueryAsync(WebApplication app, string query,
        string? permission = null)
    {
        var client = app.GetTestServer().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query }) };
        if (permission is not null) request.Headers.Add("X-Test-Perm", permission);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task It_is_read_like_a_pair_s_field()
    {
        var app = await StartAsync();
        try
        {
            var (status, body) = await QueryAsync(app,
                $$"""{ {{Field}}(first: 1) { nodes { carrier parcels } pageInfo { hasNextPage } } }""");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain("\"errors\"", body, StringComparison.Ordinal);
            // Paged in key order: the first row by key, and a next page.
            Assert.Contains("\"carrier\":\"north\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("south", body, StringComparison.Ordinal);
            Assert.Contains("\"hasNextPage\":true", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task There_is_nothing_to_write_it_with()
    {
        var app = await StartAsync();
        try
        {
            var schema = app.Services.GetRequiredService<IyuMainServerOptions>().GraphQL;
            Assert.True(schema.IsReadOnly(Field));
            Assert.Null(schema.GetMutationPrefix(Field));

            var (_, body) = await QueryAsync(app, """mutation { createFreightSummary(input: { carrier: "x" }) { id } }""");
            Assert.Contains("\"errors\"", body, StringComparison.Ordinal);       // the schema has no mutation to run
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task The_entity_s_read_policy_guards_the_field()
    {
        var app = await StartAsync(withReadPolicy: true);
        try
        {
            var query = $$"""{ {{Field}} { nodes { carrier } } }""";

            var (_, refused) = await QueryAsync(app, query);
            Assert.Contains("AUTH_NOT_AUTHORIZED", refused, StringComparison.Ordinal);
            Assert.DoesNotContain("north", refused, StringComparison.Ordinal);

            var (_, allowed) = await QueryAsync(app, query, permission: ReadPolicy);
            Assert.DoesNotContain("\"errors\"", allowed, StringComparison.Ordinal);
            Assert.Contains("north", allowed, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task It_can_be_served_on_both_surfaces_at_once()
    {
        var app = await StartAsync(onBothSurfaces: true);
        try
        {
            var (_, graph) = await QueryAsync(app, $$"""{ {{Field}} { nodes { carrier } } }""");
            Assert.Contains("north", graph, StringComparison.Ordinal);

            using var odata = await app.GetTestServer().CreateClient().GetAsync("/$data/FreightSummaries?$filter=Parcels gt 10");
            Assert.Equal(HttpStatusCode.OK, odata.StatusCode);
            Assert.Contains("south", await odata.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public void A_read_only_set_takes_paging_and_a_policy_and_is_one_door()
    {
        var schema = new IyuGraphQLSchemaBuilder();
        schema.AddReadOnlySet<FreightSummary>(Field).Page(Field, maxPageSize: 50, defaultPageSize: 10);

        Assert.Contains(Field, schema.QueryNames);
        Assert.Equal(Field, schema.QueryNameOf(typeof(FreightSummary)));
        Assert.False(schema.IsReadOnly("unknown"));

        // The same read type behind a second field — read-only or not — would be a second door to the same rows.
        Assert.Throws<InvalidOperationException>(() => schema.AddReadOnlySet<FreightSummary>("again"));
    }
}
