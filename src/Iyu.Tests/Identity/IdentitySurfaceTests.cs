using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// Each optional identity surface is turned on by its own call. What a host that left one off sees — and what
/// one that registered a surface's store but never turned the surface on is told — driven through a started host.
/// </summary>
public class IdentitySurfaceTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Permission = "orders.read";

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var claims = new FakeUserClaimsSource();
        claims.Set("person", Permission);
        builder.Services.AddSingleton<IUserClaimsSource>(claims);
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = Key }, permissionCatalog: [Permission]);
        configure(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuIdentity();
        app.MapGet("/probe", () => "ok").RequireAuthorization(Permission);
        await app.StartAsync();
        return new Host { App = app, Http = app.GetTestClient() };
    }

    private static Action<IServiceCollection> ServiceClients(FakeIdentityStore store) => s =>
    {
        s.AddIyuServiceClients();
        s.AddSingleton<IServiceClientStore>(store);
    };

    private static async Task<HttpStatusCode> ProbeAsync(Host host, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/probe");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await host.Http.SendAsync(req);
        return res.StatusCode;
    }

    [Fact]
    public async Task A_service_client_store_without_the_surface_is_refused_at_startup_naming_the_call()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(s => s.AddSingleton<IServiceClientStore>(new FakeIdentityStore())));

        Assert.Contains(nameof(IServiceClientStore), error.Message);
        Assert.Contains(nameof(IyuIdentityServiceCollectionExtensions.AddIyuServiceClients), error.Message);
    }

    [Fact]
    public async Task A_credential_store_without_the_surface_is_refused_at_startup_naming_the_call()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(s => s.AddSingleton<IUserCredentialStore>(new FakeIdentityStore())));

        Assert.Contains(nameof(IUserCredentialStore), error.Message);
        Assert.Contains(nameof(IyuIdentityServiceCollectionExtensions.AddIyuUserCredentials), error.Message);
    }

    [Fact]
    public async Task Without_service_clients_client_credentials_is_an_unsupported_grant()
    {
        await using var host = await StartAsync(_ => { });

        using var res = await host.Http.PostAsync("/api/auth/token", new FormUrlEncodedContent(
            [new("grant_type", "client_credentials"), new("client_id", "x"), new("client_secret", "y")]));
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("unsupported_grant_type", body.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// With the surface off the management routes are not mapped; with it on, they are. Read off the routing table:
    /// an anonymous request to a route that does not exist is answered 401 by the fallback policy, like one that does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_management_endpoints_exist_only_with_service_clients_on(bool on)
    {
        await using var host = await StartAsync(on ? ServiceClients(new FakeIdentityStore()) : _ => { });

        var routes = host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(e => "/" + (e.RoutePattern.RawText ?? "").Trim('/')).ToList();

        Assert.Equal(on, routes.Any(r => r.StartsWith("/api/service-clients", StringComparison.Ordinal)));
        Assert.Contains(routes, r => r == "/api/auth/token");
    }

    /// <summary>
    /// A service-client token reaches a host without service clients only if one was issued under the same key
    /// elsewhere. Nothing there can check it against a store, so it is refused — while a person's token, which needs
    /// no store, is accepted.
    /// </summary>
    [Fact]
    public async Task A_service_client_token_is_refused_where_service_clients_are_off()
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: [Permission]);
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        store.AddClient(clientId, hash, owner, perms: [Permission]);
        await using var issuing = await StartAsync(ServiceClients(store));
        string clientToken;
        using (var scope = issuing.App.Services.CreateScope())
            clientToken = (await scope.ServiceProvider.GetRequiredService<ServiceClientTokenService>()
                .IssueClientCredentialsAsync(clientId, secret, default)).AccessToken!;
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(issuing, clientToken));

        await using var host = await StartAsync(_ => { });
        string personToken;
        using (var scope = host.App.Services.CreateScope())
            personToken = (await scope.ServiceProvider.GetRequiredService<UserAccessTokenService>().IssueAsync("person"))
                .AccessToken!;

        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(host, clientToken));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(host, personToken));
    }
}
