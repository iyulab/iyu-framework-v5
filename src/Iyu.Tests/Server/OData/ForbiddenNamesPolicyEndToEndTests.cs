using System.Net;
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
/// A <c>403</c> can say which policy the caller did not satisfy — when the host asks it to, and only to someone who is signed in.
/// Saying it is off by default: a policy's name describes how the host is protected.
/// </summary>
public sealed class ForbiddenNamesPolicyEndToEndTests
{
    private const string Set = "PolicyWidgets";
    private const string ReadPolicy = "widgets.read";

    private static async Task<WebApplication> StartAsync(bool namePolicy)
    {
        var dbName = "forbidden-names-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(ReadPolicy, p => p.RequireClaim("perm", ReadPolicy)));
        builder.Services.AddIyuMainServer<PolicyWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(PolicyWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<PolicyWidgetExt, PolicyWidget>(Set);
                options.Authorize<PolicyWidgetExt>(read: ReadPolicy);
                options.NamePolicyInForbidden = namePolicy;
            });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebApplication app, string? perm)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/$data/{Set}");
        if (perm is not null) request.Headers.Add("X-Test-Perm", perm);
        using var response = await app.GetTestClient().SendAsync(request);
        // The message as a client reads it — the JSON text escapes the apostrophes.
        var text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return (response.StatusCode, text);
        var message = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
        return (response.StatusCode, message);
    }

    [Fact]
    public async Task By_default_a_refusal_does_not_name_the_policy()
    {
        await using var app = await StartAsync(namePolicy: false);

        var (status, body) = await GetAsync(app, perm: "something-else");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.DoesNotContain(ReadPolicy, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_asked_a_signed_in_caller_who_is_refused_is_told_which_policy()
    {
        await using var app = await StartAsync(namePolicy: true);

        var (status, body) = await GetAsync(app, perm: "something-else");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal($"Forbidden. Requires policy '{ReadPolicy}' (set '{Set}').", body);
    }

    /// <summary>Someone who is not signed in is told to sign in — and nothing about how the host is protected.</summary>
    [Fact]
    public async Task A_caller_who_is_not_signed_in_is_never_told_the_policy()
    {
        await using var app = await StartAsync(namePolicy: true);

        var (status, body) = await GetAsync(app, perm: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.DoesNotContain(ReadPolicy, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_who_satisfies_the_policy_is_not_refused()
    {
        await using var app = await StartAsync(namePolicy: true);

        var (status, _) = await GetAsync(app, perm: ReadPolicy);

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
