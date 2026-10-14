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

        public async Task<Tokens> MobileSignInFromAsync(string provider, string sid)
        {
            using var response = await Http.PostAsync($"/mobile-sign-in-from?provider={provider}&sid={sid}", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<Tokens>())!;
        }

        /// <summary>What a provider's logout notice does, once the application has verified it.</summary>
        public async Task EndProviderSessionAsync(string provider, string sid)
        {
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SessionEndService>().EndProviderSessionAsync(provider, sid);
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
    /// <param name="reuseInterval"><see cref="IdentityTokenOptions.RefreshTokenReuseInterval"/>; off when not given.</param>
    private static async Task<Host> StartAsync(bool ends = true, TimeSpan? reuseInterval = null)
    {
        var accounts = new FakeUserClaimsSource();
        accounts.Set(Account, "orders.read");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        builder.Services.AddSingleton<IRefreshTokenStore, FakeRefreshTokenStore>();
        builder.Services.AddIyuIdentity(new IdentityTokenOptions
        {
            SigningKey = new string('k', 32),
            RefreshTokenReuseInterval = reuseInterval ?? TimeSpan.Zero,
        }, permissionCatalog: ["orders.read"]);
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
        app.MapPost("/mobile-sign-in-from", async (string provider, string sid, UserTokenService tokens, CancellationToken ct) =>
        {
            var pair = await tokens.IssueAsync(Account, new SessionKey(provider, sid), ct);
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

    [Fact]
    public async Task A_sign_in_opened_from_a_provider_s_session_ends_with_it_at_the_next_refresh()
    {
        await using var host = await StartAsync();
        var fromProvider = await host.MobileSignInFromAsync("oidc", "p1");
        var refreshed = (await host.RefreshAsync(fromProvider.RefreshToken))!;     // carried from token to token
        var otherProviderSession = await host.MobileSignInFromAsync("oidc", "p2");
        var local = await host.MobileSignInAsync();

        await host.EndProviderSessionAsync("oidc", "p1");

        Assert.Null(await host.RefreshAsync(refreshed.RefreshToken));
        // The refused refresh ended the sign-in, and its access tokens with it.
        Assert.Equal(HttpStatusCode.Unauthorized, await host.BearerStatusAsync(refreshed.AccessToken));
        Assert.NotNull(await host.RefreshAsync(otherProviderSession.RefreshToken));
        Assert.NotNull(await host.RefreshAsync(local.RefreshToken));
    }

    [Fact]
    public async Task A_provider_s_session_names_the_provider_and_the_session()
    {
        await using var host = await StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<UserTokenService>();

        await Assert.ThrowsAsync<ArgumentException>(() => tokens.IssueAsync(Account, new SessionKey(null, "p1"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => tokens.IssueAsync(Account, new SessionKey("oidc", ""), default));
    }

    [Fact]
    public async Task A_provider_s_session_s_end_is_kept_while_a_refresh_token_opened_from_it_could_still_be_exchanged()
    {
        await using var host = await StartAsync();
        var refreshLifetime = host.App.Services.GetRequiredService<IdentityTokenOptions>().RefreshTokenLifetime;
        await host.MobileSignInFromAsync("oidc", "p1");
        var before = DateTimeOffset.UtcNow;

        await host.EndProviderSessionAsync("oidc", "p1");

        // The newest refresh token of a sign-in opened from that session is exchangeable for its whole lifetime: an end
        // forgotten sooner lets a token left unused that long refresh after the provider said the session was over.
        Assert.True(host.Store!.Ended[new SessionKey("oidc", "p1")] >= before + refreshLifetime);
    }

    [Fact]
    public async Task A_refresh_presented_again_within_the_reuse_interval_is_refused_once_the_provider_s_session_has_ended()
    {
        await using var host = await StartAsync(reuseInterval: TimeSpan.FromMinutes(1));
        var fromProvider = await host.MobileSignInFromAsync("oidc", "p1");
        var refreshed = (await host.RefreshAsync(fromProvider.RefreshToken))!;

        await host.EndProviderSessionAsync("oidc", "p1");

        // A retry of the exchange just made is the one way past the check a fresh token meets.
        Assert.Null(await host.RefreshAsync(fromProvider.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.BearerStatusAsync(refreshed.AccessToken));
    }

    [Fact]
    public async Task A_sign_in_is_not_opened_from_a_provider_s_session_that_has_ended()
    {
        await using var host = await StartAsync();
        await host.EndProviderSessionAsync("oidc", "p1");
        await using var scope = host.App.Services.CreateAsyncScope();

        var pair = await scope.ServiceProvider.GetRequiredService<UserTokenService>()
            .IssueAsync(Account, new SessionKey("oidc", "p1"), default);

        Assert.False(pair.Ok);
        Assert.Null(pair.RefreshToken);
    }

    private static string Sid(string accessToken)
        => new JsonWebToken(accessToken).TryGetPayloadValue<string>("sid", out var sid) ? sid : throw new Xunit.Sdk.XunitException("no sid");
}
