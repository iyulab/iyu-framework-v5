using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;
using static Iyu.Tests.Identity.OidcStandIn;

namespace Iyu.Tests.Identity;

/// <summary>
/// The end of a session sticks (<c>AddIyuSessionEnds</c>): signing out, signing in again and revoking a refresh token
/// end the session itself — a copy of the cookie, or an access token issued in that sign-in, is refused — and only that
/// session ends (OWASP ASVS 5.0, 7.4.1 and 7.2.4).
/// </summary>
public class SessionEndTests
{
    private const string Account = "u1";

    /// <summary>The distributed-cache store, with what it was told to keep and until when.</summary>
    private sealed class RecordingStore(IDistributedCache cache) : ISessionEndStore
    {
        private readonly DistributedCacheSessionEndStore _inner = new(cache);
        public Dictionary<SessionKey, DateTimeOffset> Ended { get; } = [];

        public Task RecordEndedAsync(SessionKey session, DateTimeOffset keepUntil, CancellationToken ct)
        {
            Ended[session] = keepUntil;
            return _inner.RecordEndedAsync(session, keepUntil, ct);
        }

        public Task<bool> HasEndedAsync(SessionKey session, CancellationToken ct) => _inner.HasEndedAsync(session, ct);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken);

    private sealed record Host(WebApplication App, HttpClient Http, RecordingStore? Store) : IAsyncDisposable
    {
        public async Task<string> SignInAsync(string? cookies = null, string path = "/sign-in")
        {
            using var response = await Http.SendAsync(With(HttpMethod.Post, path, cookies ?? ""));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return CookiesFrom(response);
        }

        public async Task SignOutAsync(string cookies)
        {
            using var response = await Http.SendAsync(With(HttpMethod.Post, "/sign-out", cookies));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<HttpStatusCode> CookieStatusAsync(string cookies)
        {
            using var response = await Http.SendAsync(With(HttpMethod.Get, "/whoami", cookies));
            return response.StatusCode;
        }

        public async Task<Tokens> MobileSignInAsync()
        {
            using var response = await Http.PostAsync("/mobile-sign-in", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<Tokens>())!;
        }

        public async Task<Tokens?> RefreshAsync(string refreshToken)
        {
            using var response = await Http.PostAsync("/mobile-refresh?token=" + Uri.EscapeDataString(refreshToken), null);
            return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<Tokens>() : null;
        }

        public async Task MobileSignOutAsync(string refreshToken)
        {
            using var response = await Http.PostAsync("/mobile-sign-out?token=" + Uri.EscapeDataString(refreshToken), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<HttpStatusCode> BearerStatusAsync(string accessToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Http.SendAsync(request);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    /// <param name="ends">Whether the application calls <c>AddIyuSessionEnds</c> (with a store).</param>
    private static async Task<Host> StartAsync(bool ends = true)
    {
        var accounts = new FakeUserClaimsSource();
        accounts.Set(Account, "orders.read");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        builder.Services.AddSingleton<IRefreshTokenStore, FakeRefreshTokenStore>();
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = new string('k', 32) }, permissionCatalog: ["orders.read"]);
        builder.Services.AddIyuRefreshTokens();
        builder.Services.AddDistributedMemoryCache();
        if (ends)
        {
            builder.Services.AddIyuSessionEnds();
            builder.Services.AddSingleton<RecordingStore>();
            builder.Services.AddSingleton<ISessionEndStore>(sp => sp.GetRequiredService<RecordingStore>());
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/whoami", (ClaimsPrincipal user) => IyuIdentityClaims.FindSubject(user) ?? "(none)");
        app.MapPost("/sign-in", async (HttpContext http, UserSessionService sessions)
            => await sessions.SignInAsync(http, Account) ? Results.Ok() : Results.Unauthorized()).AllowAnonymous();
        app.MapPost("/sign-in-persistent", async (HttpContext http, UserSessionService sessions)
            => await sessions.SignInAsync(http, Account, new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            }) ? Results.Ok() : Results.Unauthorized()).AllowAnonymous();
        app.MapPost("/sign-out", async (HttpContext http, UserSessionService sessions) =>
        {
            await sessions.SignOutAsync(http);
            return Results.Ok();
        }).AllowAnonymous();
        app.MapPost("/mobile-sign-in", async (UserTokenService tokens, CancellationToken ct) =>
        {
            var pair = await tokens.IssueAsync(Account, ct);
            return Results.Ok(new Tokens(pair.AccessToken!, pair.RefreshToken!));
        }).AllowAnonymous();
        app.MapPost("/mobile-refresh", async (string token, UserTokenService tokens, CancellationToken ct) =>
        {
            var pair = await tokens.RefreshAsync(token, ct);
            return pair.Ok ? Results.Ok(new Tokens(pair.AccessToken!, pair.RefreshToken!)) : Results.BadRequest();
        }).AllowAnonymous();
        app.MapPost("/mobile-sign-out", async (string token, UserTokenService tokens, CancellationToken ct) =>
        {
            await tokens.RevokeAsync(token, ct);
            return Results.Ok();
        }).AllowAnonymous();

        await app.StartAsync();
        var http = app.GetTestServer().CreateClient();
        http.BaseAddress = new Uri("https://localhost");
        return new Host(app, http, ends ? app.Services.GetRequiredService<RecordingStore>() : null);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_so_a_copy_of_its_cookie_is_refused()
    {
        await using var host = await StartAsync();
        var session = await host.SignInAsync();
        Assert.Equal(HttpStatusCode.OK, await host.CookieStatusAsync(session));

        await host.SignOutAsync(session);

        // The browser was told to drop the cookie; this is the same value, presented again — kept elsewhere.
        Assert.Equal(HttpStatusCode.Unauthorized, await host.CookieStatusAsync(session));
    }

    [Fact]
    public async Task Signing_out_one_session_leaves_the_account_s_other_sessions()
    {
        // What makes this a session's end and not the account's (the security stamp ends every session).
        await using var host = await StartAsync();
        var laptop = await host.SignInAsync();
        var phone = await host.SignInAsync();
        var app = await host.MobileSignInAsync();

        await host.SignOutAsync(laptop);

        Assert.Equal(HttpStatusCode.Unauthorized, await host.CookieStatusAsync(laptop));
        Assert.Equal(HttpStatusCode.OK, await host.CookieStatusAsync(phone));
        Assert.Equal(HttpStatusCode.OK, await host.BearerStatusAsync(app.AccessToken));
    }

    [Fact]
    public async Task Revoking_a_refresh_token_ends_the_access_tokens_of_its_sign_in_and_no_other()
    {
        await using var host = await StartAsync();
        var signedOut = await host.MobileSignInAsync();
        var other = await host.MobileSignInAsync();
        Assert.Equal(HttpStatusCode.OK, await host.BearerStatusAsync(signedOut.AccessToken));

        await host.MobileSignOutAsync(signedOut.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, await host.BearerStatusAsync(signedOut.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await host.BearerStatusAsync(other.AccessToken));
    }

    [Fact]
    public async Task A_refreshed_token_belongs_to_the_same_sign_in_and_ends_with_it()
    {
        await using var host = await StartAsync();
        var first = await host.MobileSignInAsync();
        var refreshed = (await host.RefreshAsync(first.RefreshToken))!;
        Assert.Equal(Sid(first.AccessToken), Sid(refreshed.AccessToken));

        await host.MobileSignOutAsync(refreshed.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, await host.BearerStatusAsync(first.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.BearerStatusAsync(refreshed.AccessToken));
    }

    [Fact]
    public async Task A_reused_refresh_token_ends_the_access_tokens_of_its_sign_in_too()
    {
        // Reuse means a copy is in someone else's hands; the access tokens it may have got go with the family.
        await using var host = await StartAsync();
        var first = await host.MobileSignInAsync();
        var refreshed = (await host.RefreshAsync(first.RefreshToken))!;

        Assert.Null(await host.RefreshAsync(first.RefreshToken));     // presented again

        Assert.Equal(HttpStatusCode.Unauthorized, await host.BearerStatusAsync(refreshed.AccessToken));
    }

    [Fact]
    public async Task Signing_in_again_ends_the_session_the_request_carried()
    {
        await using var host = await StartAsync();
        var before = await host.SignInAsync();

        var after = await host.SignInAsync(before);

        Assert.Equal(HttpStatusCode.Unauthorized, await host.CookieStatusAsync(before));
        Assert.Equal(HttpStatusCode.OK, await host.CookieStatusAsync(after));
    }

    [Fact]
    public async Task An_access_token_issued_on_its_own_names_no_session()
    {
        await using var host = await StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var issued = await scope.ServiceProvider.GetRequiredService<UserAccessTokenService>().IssueAsync(Account);

        Assert.False(new JsonWebToken(issued.AccessToken!).TryGetPayloadValue<string>("sid", out _));
        Assert.Equal(HttpStatusCode.OK, await host.BearerStatusAsync(issued.AccessToken!));
    }

    [Fact]
    public async Task Without_the_call_signing_out_and_revoking_end_only_the_cookie_and_the_refresh_token()
    {
        await using var host = await StartAsync(ends: false);
        var session = await host.SignInAsync();
        var app = await host.MobileSignInAsync();

        await host.SignOutAsync(session);
        await host.MobileSignOutAsync(app.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, await host.CookieStatusAsync(session));
        Assert.Equal(HttpStatusCode.OK, await host.BearerStatusAsync(app.AccessToken));
        Assert.Null(await host.RefreshAsync(app.RefreshToken));
    }

    [Fact]
    public async Task A_cookie_s_end_is_kept_while_the_cookie_could_still_be_presented()
    {
        await using var host = await StartAsync();
        var lifetime = host.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).ExpireTimeSpan;

        var session = await host.SignInAsync();
        var signedOutAt = DateTimeOffset.UtcNow;
        await host.SignOutAsync(session);
        var persistent = await host.SignInAsync(path: "/sign-in-persistent");
        await host.SignOutAsync(persistent);

        var kept = host.Store!.Ended.Values.Order().ToArray();
        Assert.Equal(2, kept.Length);
        // A session cookie lives at most its lifetime past its last renewal; one opened with an expiry of its own,
        // until then.
        Assert.InRange(kept[0], signedOutAt + lifetime, DateTimeOffset.UtcNow + lifetime);
        Assert.InRange(kept[1], DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public async Task A_sign_in_s_end_is_kept_while_its_access_tokens_could_still_be_presented()
    {
        await using var host = await StartAsync();
        var app = await host.MobileSignInAsync();
        var before = DateTimeOffset.UtcNow;

        await host.MobileSignOutAsync(app.RefreshToken);

        var (session, keepUntil) = Assert.Single(host.Store!.Ended);
        Assert.Null(session.Provider);
        Assert.Equal(Sid(app.AccessToken), session.Id);
        Assert.InRange(keepUntil, before + TimeSpan.FromHours(1), DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
    }

    private static string Sid(string accessToken)
        => new JsonWebToken(accessToken).TryGetPayloadValue<string>("sid", out var sid) ? sid : throw new Xunit.Sdk.XunitException("no sid");
}
