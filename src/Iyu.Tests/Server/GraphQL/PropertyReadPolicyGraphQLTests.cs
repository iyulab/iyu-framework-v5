using System.Net.Http.Json;
using Iyu.MainServer;
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
/// A property read policy declared once on the host options holds on the GraphQL field too: the caller who fails it is
/// refused, whatever way the type is reached, and a value behind it never appears in the answer. The OData surface's
/// counterpart is <see cref="PropertyReadPolicyEndToEndTests"/>.
/// </summary>
public class PropertyReadPolicyGraphQLTests
{
    private const string NamesRead = "names.read";
    private const string CodesRead = "codes.read";
    private const string OrderName = "a-name-behind-the-policy";
    private const string SecretCode = "a-code-behind-the-policy";

    private static async Task<WebApplication> StartAsync(bool odata = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(NamesRead, p => p.RequireClaim("perm", NamesRead));
            o.AddPolicy(CodesRead, p => p.RequireClaim("perm", CodesRead));
        });
        var dbName = "prop-gql-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<NavContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(NavOrdersController).Assembly);
                if (odata)
                {
                    options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>("NavOrders");
                    options.ODataModel.AddEntityPair<NavSecretExt, NavSecret>("NavSecrets");
                }
                options.GraphQL.AddEntityPair<NavOrderExt, NavOrder>("navOrders", "navOrder");
                options.GraphQL.AddEntityPair<NavSecretExt, NavSecret>("navSecrets", "navSecret");
                options.AuthorizeProperty<NavOrderExt>(x => x.Name, NamesRead);
                options.AuthorizeProperty<NavSecretExt>(x => x.Code, CodesRead);
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<NavContext>();
        var secretId = Guid.NewGuid();
        ctx.SecretsExt.Add(new NavSecretExt { Id = secretId, Code = SecretCode });
        ctx.OrdersExt.Add(new NavOrderExt { Id = Guid.NewGuid(), Name = OrderName, SecretId = secretId });
        await ctx.SaveChangesAsync();
        return app;
    }

    private static async Task<string> QueryAsync(WebApplication app, string query, params string[] perms)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new { query }),
        };
        foreach (var perm in perms) request.Headers.Add("X-Test-Perm", perm);
        using var response = await app.GetTestServer().CreateClient().SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    public static TheoryData<string> WaysToReadTheName => new()
    {
        "{ navOrders { nodes { name } } }",
        "{ navOrders { nodes { id name } } }",
        "{ navOrders { nodes { id secret { id } name } } }",
        "{ navOrders { edges { node { name } } } }",
        "{ a: navOrders { nodes { name } } b: navOrders { nodes { id } } }",
        "{ navOrders { nodes { ...F } } } fragment F on NavOrderExt { name }",
        "{ navOrders { nodes { ... on NavOrderExt { name } } } }",
    };

    [Theory]
    [MemberData(nameof(WaysToReadTheName))]
    public async Task A_caller_who_fails_the_policy_never_gets_the_value(string query)
    {
        var app = await StartAsync();
        try
        {
            var answer = await QueryAsync(app, query);
            Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", answer, StringComparison.Ordinal);
            Assert.DoesNotContain(OrderName, answer, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_caller_who_holds_the_policy_reads_it()
    {
        var app = await StartAsync();
        try
        {
            var answer = await QueryAsync(app, "{ navOrders { nodes { name } } }", NamesRead);
            Assert.Contains(OrderName, answer, StringComparison.Ordinal);
            Assert.DoesNotContain("\"errors\"", answer, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The policy sits on the one property: the rest of the row stays readable, so naming what you may read works.</summary>
    [Fact]
    public async Task The_rest_of_the_row_stays_readable()
    {
        var app = await StartAsync();
        try
        {
            var answer = await QueryAsync(app, "{ navOrders { nodes { id } } }");
            Assert.DoesNotContain("\"errors\"", answer, StringComparison.Ordinal);
            Assert.DoesNotContain(OrderName, answer, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The target is reached through a navigation, with no query field of its own in the request.</summary>
    [Fact]
    public async Task A_property_behind_a_navigation_is_guarded_by_its_own_policy()
    {
        var app = await StartAsync();
        try
        {
            var denied = await QueryAsync(app, "{ navOrders { nodes { secret { code } } } }", NamesRead);
            Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", denied, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretCode, denied, StringComparison.Ordinal);

            var allowed = await QueryAsync(app, "{ navOrders { nodes { secret { code } } } }", CodesRead);
            Assert.Contains(SecretCode, allowed, StringComparison.Ordinal);
            Assert.DoesNotContain("\"errors\"", allowed, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>No OData set serves the type; the declaration is taken by the surface that does.</summary>
    [Fact]
    public async Task A_type_served_by_GraphQL_alone_takes_a_property_policy()
    {
        var app = await StartAsync(odata: false);
        try
        {
            var answer = await QueryAsync(app, "{ navOrders { nodes { name } } }");
            Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", answer, StringComparison.Ordinal);
            Assert.DoesNotContain(OrderName, answer, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public void A_policy_for_a_property_the_type_does_not_have_stops_the_host()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddIyuMainServer<NavContext>(
                db => db.UseInMemoryDatabase("x"),
                options =>
                {
                    options.GraphQL.AddEntityPair<NavOrderExt, NavOrder>("navOrders", "navOrder");
                    options.AuthorizeProperty(typeof(NavOrderExt), "NoSuchProperty", NamesRead);
                }));
        Assert.Contains("NoSuchProperty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_policy_for_a_navigation_stops_the_host()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddIyuMainServer<NavContext>(
                db => db.UseInMemoryDatabase("x"),
                options =>
                {
                    options.GraphQL.AddEntityPair<NavOrderExt, NavOrder>("navOrders", "navOrder");
                    options.GraphQL.AddEntityPair<NavSecretExt, NavSecret>("navSecrets", "navSecret");
                    options.AuthorizeProperty<NavOrderExt>(x => x.Secret, NamesRead);
                }));
        Assert.Contains("navigation", error.Message, StringComparison.Ordinal);
    }
}
