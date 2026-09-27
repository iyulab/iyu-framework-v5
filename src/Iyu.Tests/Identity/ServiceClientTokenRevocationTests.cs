using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// Revoking, rotating or re-scoping a service client must reach the access tokens already issued
/// to it — not only the next issuance. Driven through the real authentication pipeline, because
/// the check lives in the bearer handler's validation step and a handler-level test would never
/// run it.
/// </summary>
public class ServiceClientTokenRevocationTests
{
    private const string Permission = "orders.write";

    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required FakeIdentityStore Store { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public required Guid OwnerId { get; init; }

        public async Task<(Guid Id, string Token)> IssueAsync(DateTimeOffset? expiresAt = null)
        {
            var (clientId, secret, hash) = ServiceClientSecrets.Generate();
            var id = Store.AddClient(clientId, hash, OwnerId, expiresAt: expiresAt, perms: [Permission]);
            using var scope = App.Services.CreateScope();
            var tokens = scope.ServiceProvider.GetRequiredService<IdentityTokenService>();
            var result = await tokens.IssueClientCredentialsAsync(clientId, secret, default);
            Assert.True(result.Ok);
            return (id, result.AccessToken!);
        }

        public async Task<HttpStatusCode> ProbeAsync(string token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/probe");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await Http.SendAsync(req);
            return res.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(Action<IdentityTokenOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: [Permission]);
        // Issued tokens carry nbf/exp from this clock and the bearer handler checks them against
        // the wall clock, so it has to start at "now", not at the fake's default epoch.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<IServiceClientStore>(store);
        builder.Services.AddSingleton<TimeProvider>(clock);
        var options = new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef" };
        configure?.Invoke(options);
        builder.Services.AddIyuIdentity(options, permissionCatalog: [Permission]);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuIdentity();
        app.MapGet("/probe", () => "ok").RequireAuthorization(Permission);
        await app.StartAsync();

        return new Harness { App = app, Http = app.GetTestClient(), Store = store, Clock = clock, OwnerId = owner };
    }

    private static Action<IdentityTokenOptions> NoCache => o => o.ServiceClientValidationWindow = TimeSpan.Zero;

    [Fact]
    public async Task UnchangedClient_TokenKeepsWorking()
    {
        await using var h = await StartAsync(NoCache);
        var (_, token) = await h.IssueAsync();

        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));
        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));
    }

    [Fact]
    public async Task Revoke_RejectsTokenIssuedBefore()
    {
        await using var h = await StartAsync(NoCache);
        var (id, token) = await h.IssueAsync();
        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));

        Assert.True(await h.Store.DeactivateAsync(id, h.OwnerId, default));

        Assert.Equal(HttpStatusCode.Unauthorized, await h.ProbeAsync(token));
    }

    [Fact]
    public async Task Rotate_RejectsTokenIssuedBefore()
    {
        await using var h = await StartAsync(NoCache);
        var (id, token) = await h.IssueAsync();
        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));

        using (var scope = h.App.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<ServiceClientService>()
                .RotateAsync(id, h.OwnerId, default)).Ok);

        Assert.Equal(HttpStatusCode.Unauthorized, await h.ProbeAsync(token));
    }

    [Fact]
    public async Task PermissionsReplaced_RejectsTokenIssuedBefore()
    {
        await using var h = await StartAsync(NoCache);
        var (id, token) = await h.IssueAsync();
        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));

        // Narrowing is the case that matters: the old token still names the removed permission.
        Assert.True(await h.Store.UpdatePermissionsAsync(id, h.OwnerId, [], default));

        Assert.Equal(HttpStatusCode.Unauthorized, await h.ProbeAsync(token));
    }

    [Fact]
    public async Task ClientExpiry_RejectsTokenStillWithinItsOwnLifetime()
    {
        await using var h = await StartAsync(NoCache);
        var (_, token) = await h.IssueAsync(expiresAt: h.Clock.GetUtcNow().AddMinutes(10));
        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));

        h.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(HttpStatusCode.Unauthorized, await h.ProbeAsync(token));
    }

    [Fact]
    public async Task DefaultWindow_ReusesARecentCheck_ThenRechecks()
    {
        await using var h = await StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), h.App.Services.GetRequiredService<IdentityTokenOptions>().ServiceClientValidationWindow);
        var (id, token) = await h.IssueAsync();
        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));

        Assert.True(await h.Store.DeactivateAsync(id, h.OwnerId, default));

        // Inside the window the earlier positive check stands — that is the documented delay.
        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.Unauthorized, await h.ProbeAsync(token));
    }

    [Fact]
    public async Task ValidationTurnedOff_TokenOutlivesRevocation()
    {
        await using var h = await StartAsync(o => o.ValidateServiceClientTokens = false);
        var (id, token) = await h.IssueAsync();
        Assert.True(await h.Store.DeactivateAsync(id, h.OwnerId, default));

        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(token));
    }

    [Fact]
    public async Task UserToken_IsNotSubjectToTheServiceClientCheck()
    {
        await using var h = await StartAsync(NoCache);
        using var scope = h.App.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IdentityTokenService>();
        var user = tokens.IssueUserToken([new Claim("sub", "someone"), IyuIdentityClaims.Permission(Permission)]);

        Assert.Equal(HttpStatusCode.OK, await h.ProbeAsync(user.AccessToken!));
    }
}
