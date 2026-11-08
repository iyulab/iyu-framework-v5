using System.Net;
using System.Net.Http.Json;
using Iyu.MainServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// The row a <c>POST</c> echoes is a read of the entity re-read through the read type, which can carry columns the caller
/// never sent. A caller who may write but not read the set gets the <c>Location</c> and no body — the entity's read
/// policy and the write policy are separate requirements, and the write must stay possible.
/// </summary>
public class PostEchoReadPolicyTests
{
    private const string ReadPolicy = "orders.read";
    private const string WritePolicy = "orders.write";

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(ReadPolicy, p => p.RequireClaim("perm", ReadPolicy));
            o.AddPolicy(WritePolicy, p => p.RequireClaim("perm", WritePolicy));
        });
        var dbName = "post-echo-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<NavContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(NavOrdersController).Assembly);
                options.ODataModel.AddEntityPair<NavOrderExt, NavOrder>("NavOrders");
                options.ODataModel.AddEntityPair<NavSecretExt, NavSecret>("NavSecrets");
                options.Authorize<NavOrderExt>(read: ReadPolicy, write: WritePolicy);
            });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, params string[] perms)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/$data/NavOrders")
        {
            Content = JsonContent.Create(new { Name = "an-order" }),
        };
        foreach (var perm in perms) request.Headers.Add("X-Test-Perm", perm);
        return await app.GetTestServer().CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task A_caller_who_may_write_but_not_read_gets_the_location_and_no_body()
    {
        var app = await StartAsync();
        try
        {
            using var response = await PostAsync(app, WritePolicy);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_caller_who_may_write_and_read_gets_the_row()
    {
        var app = await StartAsync();
        try
        {
            using var response = await PostAsync(app, WritePolicy, ReadPolicy);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Contains("an-order", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_set_with_no_read_policy_echoes_as_before()
    {
        var app = await StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/$data/NavSecrets")
            {
                Content = JsonContent.Create(new { Code = "a-code" }),
            };
            using var response = await app.GetTestServer().CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Contains("a-code", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }
}
