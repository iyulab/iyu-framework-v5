using System.Net;
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

namespace Iyu.Tests.Server;

/// <summary>
/// An entity's policies are declared once — <see cref="IyuMainServerOptions.Authorize{TRead}"/> — and every surface
/// that serves it enforces them. Declared per surface, a second surface was a second place to forget the policy, and
/// the forgotten one stayed open under the fallback policy.
/// </summary>
public class EntityPolicyEndToEndTests
{
    private const string ReadPolicy = "widgets.read";
    private const string WritePolicy = "widgets.write";

    private static async Task<WebApplication> StartAsync(Action<IyuMainServerOptions> register)
    {
        var dbName = "entity-policy-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(opts =>
        {
            opts.AddPolicy(ReadPolicy, p => p.RequireClaim("perm", ReadPolicy));
            opts.AddPolicy(WritePolicy, p => p.RequireClaim("perm", WritePolicy));
        });
        builder.Services.AddIyuMainServer<PolicyWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(PolicyWidgetsController).Assembly);
                register(options);
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PolicyWidgetContext>().WidgetsExt
            .Add(new PolicyWidgetExt { Id = Guid.NewGuid(), Name = "protected-row" });
        await scope.ServiceProvider.GetRequiredService<PolicyWidgetContext>().SaveChangesAsync();
        return app;
    }

    private static void BothSurfaces(IyuMainServerOptions options)
    {
        options.ODataModel.AddEntityPair<PolicyWidgetExt, PolicyWidget>("PolicyWidgets");
        options.GraphQL.AddEntityPair<PolicyWidgetExt, PolicyWidget>("policyWidgets", "policyWidget");
        options.Authorize<PolicyWidgetExt>(read: ReadPolicy, write: WritePolicy);
    }

    private static async Task<(HttpStatusCode OData, string GraphQL)> ReadBothAsync(HttpClient client, string? perm)
    {
        using var odata = new HttpRequestMessage(HttpMethod.Get, "/$data/PolicyWidgets");
        using var graphql = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new { query = "{ policyWidgets { nodes { name } } }" }),
        };
        if (perm is not null)
        {
            odata.Headers.Add("X-Test-Perm", perm);
            graphql.Headers.Add("X-Test-Perm", perm);
        }
        using var o = await client.SendAsync(odata);
        using var g = await client.SendAsync(graphql);
        return (o.StatusCode, await g.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_declaration_guards_the_OData_set_and_the_GraphQL_field_alike()
    {
        var app = await StartAsync(BothSurfaces);
        try
        {
            var client = app.GetTestServer().CreateClient();

            var (odataDenied, graphqlDenied) = await ReadBothAsync(client, perm: "something.else");
            Assert.Equal(HttpStatusCode.Forbidden, odataDenied);
            Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", graphqlDenied, StringComparison.Ordinal);
            Assert.DoesNotContain("protected-row", graphqlDenied, StringComparison.Ordinal);

            var (odataAllowed, graphqlAllowed) = await ReadBothAsync(client, perm: ReadPolicy);
            Assert.Equal(HttpStatusCode.OK, odataAllowed);
            Assert.Contains("protected-row", graphqlAllowed, StringComparison.Ordinal);
            Assert.DoesNotContain("\"errors\"", graphqlAllowed, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task The_write_policy_guards_OData_writes()
    {
        var app = await StartAsync(BothSurfaces);
        try
        {
            using var post = new HttpRequestMessage(HttpMethod.Post, "/$data/PolicyWidgets")
            {
                Content = JsonContent.Create(new { Name = "new" }),
            };
            post.Headers.Add("X-Test-Perm", ReadPolicy);
            using var resp = await app.GetTestServer().CreateClient().SendAsync(post);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_type_served_by_GraphQL_alone_takes_a_declaration()
    {
        var app = await StartAsync(options =>
        {
            options.GraphQL.AddEntityPair<PolicyWidgetExt, PolicyWidget>("policyWidgets", "policyWidget");
            options.Authorize<PolicyWidgetExt>(read: ReadPolicy);
        });
        try
        {
            var (_, graphql) = await ReadBothAsync(app.GetTestServer().CreateClient(), perm: "something.else");
            Assert.Contains("\"AUTH_NOT_AUTHORIZED\"", graphql, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_declaration_for_a_type_no_surface_serves_is_refused()
    {
        var error = await Record.ExceptionAsync(() => StartAsync(options =>
            options.Authorize<PolicyWidgetExt>(read: ReadPolicy)));

        var refused = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(nameof(PolicyWidgetExt), refused.Message);
    }

    /// <summary>A map of read types to policies — what a generator emits — declares through the non-generic form.</summary>
    [Fact]
    public async Task A_read_type_known_at_run_time_declares_the_same_way()
    {
        var map = new Dictionary<Type, (string Read, string Write)> { [typeof(PolicyWidgetExt)] = (ReadPolicy, WritePolicy) };
        var app = await StartAsync(options =>
        {
            options.ODataModel.AddEntityPair<PolicyWidgetExt, PolicyWidget>("PolicyWidgets");
            foreach (var (type, (read, write)) in map) options.Authorize(type, read: read, write: write);
        });
        try
        {
            var (odata, _) = await ReadBothAsync(app.GetTestServer().CreateClient(), perm: "something.else");
            Assert.Equal(HttpStatusCode.Forbidden, odata);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public void A_second_declaration_for_one_type_is_refused()
    {
        var options = new IyuMainServerOptions();
        options.Authorize<PolicyWidgetExt>(read: ReadPolicy);
        Assert.Throws<InvalidOperationException>(() => options.Authorize<PolicyWidgetExt>(write: WritePolicy));
    }
}
