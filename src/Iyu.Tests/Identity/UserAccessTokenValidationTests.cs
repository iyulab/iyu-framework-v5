using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// A person's access token checked against the account on every request
/// (<see cref="IdentityTokenOptions.ValidateUserAccessTokens"/>): the two things that end a session cookie at its
/// revalidation — the account is refused, or its security stamp changed — end a bearer token too, without waiting for
/// it to expire. Off, a token is not looked at again while it lives.
/// </summary>
public class UserAccessTokenValidationTests
{
    private sealed record Host(WebApplication App, HttpClient Http, FakeUserClaimsSource Accounts) : IAsyncDisposable
    {
        /// <summary>An access token for <paramref name="subject"/>, as the application's own sign-in would issue it.</summary>
        public async Task<string> TokenAsync(string subject)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var issued = await scope.ServiceProvider.GetRequiredService<UserAccessTokenService>().IssueAsync(subject);
            Assert.True(issued.Ok);
            return issued.AccessToken!;
        }

        public async Task<HttpStatusCode> StatusAsync(string token, string path = "/whoami")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Host> StartAsync(bool validate)
    {
        var accounts = new FakeUserClaimsSource();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        builder.Services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = new string('k', 32), ValidateUserAccessTokens = validate },
            permissionCatalog: ["orders.read"]);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/whoami", (ClaimsPrincipal user) => IyuIdentityClaims.FindSubject(user) ?? "(none)");
        app.MapGet("/orders", () => "orders").RequireAuthorization("orders.read");
        await app.StartAsync();
        return new Host(app, app.GetTestServer().CreateClient(), accounts);
    }

    [Fact]
    public async Task A_token_issued_before_the_stamp_changed_is_refused_and_one_issued_after_is_not()
    {
        await using var host = await StartAsync(validate: true);
        host.Accounts.Set("u1", "orders.read");
        host.Accounts.Stamp("u1", "stamp-1");
        var before = await host.TokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(before, "/orders"));

        host.Accounts.Stamp("u1", "stamp-2");                       // the password was changed

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(before, "/orders"));
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(await host.TokenAsync("u1"), "/orders"));
    }

    [Fact]
    public async Task A_token_is_refused_once_the_source_declines_the_account()
    {
        await using var host = await StartAsync(validate: true);
        host.Accounts.Set("u1", "orders.read");
        var token = await host.TokenAsync("u1");
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(token));

        host.Accounts.Set("u1", null);                              // deactivated

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(token));
    }

    [Fact]
    public async Task The_comparison_runs_both_ways_as_it_does_for_a_session()
    {
        await using var host = await StartAsync(validate: true);
        host.Accounts.Set("u1", "orders.read");
        var unstamped = await host.TokenAsync("u1");

        host.Accounts.Stamp("u1", "stamp-1");                       // the source starts answering a stamp
        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(unstamped));

        var stamped = await host.TokenAsync("u1");
        host.Accounts.Unstamp("u1");                                // and stops
        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(stamped));
    }

    [Fact]
    public async Task What_the_token_says_is_not_replaced_only_whether_it_still_belongs_to_the_account()
    {
        // A narrowed permission arrives at the next refresh, as before: the check ends a token, it does not rewrite one.
        await using var host = await StartAsync(validate: true);
        host.Accounts.Set("u1", "orders.read");
        var token = await host.TokenAsync("u1");

        host.Accounts.Set("u1");                                    // the permission is withdrawn; the account stands

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(token, "/orders"));
    }

    [Fact]
    public async Task A_service_client_s_token_is_its_client_s_and_meets_neither_of_a_person_s_checks()
    {
        // Both person checks on: the account check would ask the claims source about the client's id — no person — and
        // refuse; the session check would look for a sign-in the token does not name. The client's own check decides.
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "Owner", perms: ["orders.read"]);
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        store.AddClient(clientId, hash, owner, perms: ["orders.read"]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IServiceClientStore>(store);
        builder.Services.AddSingleton(store.Claims);
        builder.Services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = new string('k', 32), ValidateUserAccessTokens = true },
            permissionCatalog: ["orders.read"]);
        builder.Services.AddIyuServiceClients();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<ISessionEndStore, DistributedCacheSessionEndStore>();
        builder.Services.AddIyuSessionEnds();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/orders", () => "orders").RequireAuthorization("orders.read");
        await app.StartAsync();
        using var http = app.GetTestServer().CreateClient();

        string token;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var issued = await scope.ServiceProvider.GetRequiredService<ServiceClientTokenService>()
                .IssueClientCredentialsAsync(clientId, secret, default);
            Assert.True(issued.Ok);
            token = issued.AccessToken!;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Left_off_a_token_is_not_looked_at_again_while_it_lives()
    {
        await using var host = await StartAsync(validate: false);
        host.Accounts.Set("u1", "orders.read");
        host.Accounts.Stamp("u1", "stamp-1");
        var token = await host.TokenAsync("u1");

        host.Accounts.Stamp("u1", "stamp-2");
        host.Accounts.Set("u1", null);

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(token, "/orders"));
    }
}
