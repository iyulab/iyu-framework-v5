using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Iyu.Tests.Identity.OidcStandIn;

namespace Iyu.Tests.Identity;

/// <summary>
/// Service clients seen by someone other than their owner: an administrator lists every owner's clients and revokes
/// any of them, or all of one owner's at once (<c>MapIyuServiceClientAdministration</c>) — the step after a password
/// reset that finds the credentials a compromised account issued. Who is an administrator is the host's policy.
/// </summary>
public class ServiceClientAdministrationTests
{
    private const string AdminPermission = "identity.admin";
    private const string OrdersRead = "orders.read";

    private sealed record Host(WebApplication App, HttpClient Http, FakeIdentityStore Store, Guid Admin, Guid Owner, Guid Other)
        : IAsyncDisposable
    {
        public async Task<string> CookieAsync(Guid account)
        {
            using var response = await Http.PostAsync($"/sign-in?subject={account}", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return CookiesFrom(response);
        }

        public async Task<string> BearerAsync(Guid account)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var issued = await scope.ServiceProvider.GetRequiredService<UserAccessTokenService>().IssueAsync(account.ToString());
            return issued.AccessToken!;
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? cookies = null, string? bearer = null,
            HttpContent? content = null)
        {
            var request = With(method, path, cookies ?? "", content);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return Http.SendAsync(request);
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Host> StartAsync(Action<WebApplication> map)
    {
        var store = new FakeIdentityStore();
        var admin = store.AddUser("admin", "Admin", perms: [AdminPermission, OrdersRead]);
        var owner = store.AddUser("owner", "Owner", perms: [OrdersRead]);
        var other = store.AddUser("other", "Other", perms: [OrdersRead]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IServiceClientStore>(store);
        builder.Services.AddSingleton(store.Claims);
        // Every request asks the store: a revocation reaches a token on the next request, not after the window.
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = new string('k', 32), ServiceClientValidationWindow = TimeSpan.Zero },
            permissionCatalog: [AdminPermission, OrdersRead]);
        builder.Services.AddIyuServiceClients();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/sign-in", async (string subject, HttpContext http, UserSessionService sessions)
            => await sessions.SignInAsync(http, subject) ? Results.Ok() : Results.Unauthorized()).AllowAnonymous();
        app.MapGet("/orders", () => "orders").RequireAuthorization(OrdersRead);
        map(app);
        await app.StartAsync();
        var http = app.GetTestServer().CreateClient();
        http.BaseAddress = new Uri("https://localhost");
        return new Host(app, http, store, admin, owner, other);
    }

    private static readonly JsonContent NewClient = JsonContent.Create(new { displayName = "tool", permissions = new[] { OrdersRead } });

    private static Task<Host> StartAdministeredAsync() => StartAsync(app =>
    {
        app.MapIyuIdentity();
        app.MapIyuServiceClientAdministration(AdminPermission);
    });

    /// <summary>A client <paramref name="owner"/> issues through their own endpoint, and a token it obtains.</summary>
    private static async Task<(Guid Id, string Token)> IssueAsync(Host host, Guid owner)
    {
        using var created = await host.SendAsync(HttpMethod.Post, "/api/service-clients", cookies: await host.CookieAsync(owner),
            content: JsonContent.Create(new { displayName = "tool", permissions = new[] { OrdersRead } }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        await using var scope = host.App.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<ServiceClientTokenService>().IssueClientCredentialsAsync(
            body.RootElement.GetProperty("clientId").GetString()!, body.RootElement.GetProperty("secret").GetString()!, default);
        Assert.True(token.Ok);
        return (id, token.AccessToken!);
    }

    private static async Task<JsonElement[]> ListAsync(Host host, string query = "")
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/api/admin/service-clients" + query, cookies: await host.CookieAsync(host.Admin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public async Task An_administrator_lists_every_owner_s_clients_with_their_owner_and_no_secret()
    {
        await using var host = await StartAdministeredAsync();
        var (ownersClient, _) = await IssueAsync(host, host.Owner);
        var (othersClient, _) = await IssueAsync(host, host.Other);

        var all = await ListAsync(host);
        var mine = await ListAsync(host, $"?owner={host.Owner}");

        Assert.Equal(
            new Dictionary<Guid, Guid> { [ownersClient] = host.Owner, [othersClient] = host.Other },
            all.ToDictionary(c => c.GetProperty("id").GetGuid(), c => c.GetProperty("ownerUserId").GetGuid()));
        Assert.All(all, c => Assert.DoesNotContain(c.EnumerateObject(), p => p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                                                                             && p.Name != "secretRotatedAt"));
        Assert.Equal(ownersClient, Assert.Single(mine).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_administrator_revokes_another_owner_s_client_and_its_token_is_refused()
    {
        await using var host = await StartAdministeredAsync();
        var (id, token) = await IssueAsync(host, host.Owner);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/orders", bearer: token)).StatusCode);

        using var revoked = await host.SendAsync(HttpMethod.Delete, $"/api/admin/service-clients/{id}", cookies: await host.CookieAsync(host.Admin));
        using var again = await host.SendAsync(HttpMethod.Delete, $"/api/admin/service-clients/{Guid.NewGuid()}", cookies: await host.CookieAsync(host.Admin));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/orders", bearer: token)).StatusCode);
        Assert.False(Assert.Single(await ListAsync(host)).GetProperty("isActive").GetBoolean());   // still listed, marked
    }

    [Fact]
    public async Task An_administrator_revokes_every_client_of_one_owner_and_no_other_owner_s()
    {
        await using var host = await StartAdministeredAsync();
        var (_, first) = await IssueAsync(host, host.Owner);
        var (_, second) = await IssueAsync(host, host.Owner);
        var (_, others) = await IssueAsync(host, host.Other);

        using var response = await host.SendAsync(HttpMethod.Delete, $"/api/admin/service-clients/owner/{host.Owner}",
            cookies: await host.CookieAsync(host.Admin));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(2, body.RootElement.GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/orders", bearer: first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/orders", bearer: second)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/orders", bearer: others)).StatusCode);
    }

    [Fact]
    public async Task Only_the_host_s_administrators_with_a_session_cookie_reach_it()
    {
        await using var host = await StartAdministeredAsync();
        var (_, clientToken) = await IssueAsync(host, host.Owner);

        using var owner = await host.SendAsync(HttpMethod.Get, "/api/admin/service-clients", cookies: await host.CookieAsync(host.Owner));
        using var adminBearer = await host.SendAsync(HttpMethod.Get, "/api/admin/service-clients", bearer: await host.BearerAsync(host.Admin));
        using var serviceClient = await host.SendAsync(HttpMethod.Get, "/api/admin/service-clients", bearer: clientToken);
        using var anonymous = await host.SendAsync(HttpMethod.Get, "/api/admin/service-clients");

        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adminBearer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, serviceClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Mapping_it_without_service_clients_is_refused()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(new FakeUserClaimsSource());
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = new string('k', 32) }, permissionCatalog: [AdminPermission]);
        await using var app = builder.Build();

        Assert.Throws<InvalidOperationException>(() => app.MapIyuServiceClientAdministration(AdminPermission));
    }

    [Fact]
    public async Task An_owner_s_own_endpoints_act_for_the_person_whose_session_cookie_it_is()
    {
        // A request carrying a session cookie and a bearer token of someone else acts for the cookie's person.
        await using var host = await StartAsync(app => app.MapGroup("").RequireAuthorization(OrdersRead).MapIyuIdentity());
        using var created = await host.SendAsync(HttpMethod.Post, "/api/service-clients",
            cookies: await host.CookieAsync(host.Owner), bearer: await host.BearerAsync(host.Other), content: NewClient);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Assert.Single(await host.Store.ListServiceClientsByOwnerAsync(host.Owner, default));
        Assert.Empty(await host.Store.ListServiceClientsByOwnerAsync(host.Other, default));
    }

    [Fact]
    public async Task Narrowing_the_owner_endpoints_with_a_permission_policy_does_not_open_them_to_a_bearer_token()
    {
        // A host narrows the management surface to its administrators by putting a permission policy on the group.
        // That policy names the bearer scheme as well as the cookie; it must not widen what the endpoints accept.
        await using var host = await StartAsync(app => app.MapGroup("").RequireAuthorization(AdminPermission).MapIyuIdentity());

        using var bearer = await host.SendAsync(HttpMethod.Post, "/api/service-clients",
            bearer: await host.BearerAsync(host.Admin), content: NewClient);
        using var cookie = await host.SendAsync(HttpMethod.Post, "/api/service-clients",
            cookies: await host.CookieAsync(host.Admin), content: NewClient);

        Assert.Equal(HttpStatusCode.Forbidden, bearer.StatusCode);
        Assert.Equal(HttpStatusCode.Created, cookie.StatusCode);
    }
}
