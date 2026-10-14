using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using static Iyu.Tests.Identity.OidcStandIn;

namespace Iyu.Tests.Identity;

/// <summary>
/// An OpenID Connect provider's back-channel logout notice, received (<c>AddIyuBackChannelLogout</c> +
/// <c>MapIyuBackChannelLogout</c>): a token the provider signed ends the sessions opened from the session it names,
/// and nothing else passes for one — OpenID Connect Back-Channel Logout 1.0, section 2.6.
/// </summary>
public class BackChannelLogoutTests
{
    private const string Endpoint = "/api/auth/external/oidc/backchannel-logout";
    private static readonly string LocalAccount = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString();

    private sealed class Linking : IExternalLoginResolver
    {
        public Task<string?> ResolveSubjectAsync(ExternalLogin login, CancellationToken ct) => Task.FromResult<string?>(LocalAccount);
    }

    private sealed record Host(WebApplication App, HttpClient Http, OidcStandIn Provider) : IAsyncDisposable
    {
        /// <summary>Posts a logout token the way a provider does: a form, one field.</summary>
        public Task<HttpResponseMessage> NotifyAsync(string logoutToken, string endpoint = Endpoint)
            => Http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = logoutToken }));

        public async Task<HttpStatusCode> SessionStatusAsync(string session)
        {
            using var response = await Http.SendAsync(With(HttpMethod.Get, "/whoami", session));
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Host> StartAsync(Action<BackChannelLogoutOptions>? configure = null,
        bool addServices = true, bool registerStore = true, Action<OpenIdConnectOptions>? handler = null)
    {
        var provider = new OidcStandIn();
        var accounts = new FakeUserClaimsSource();
        accounts.Set(LocalAccount);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        builder.Services.AddSingleton<IExternalLoginResolver>(new Linking());
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = new string('k', 32) }, permissionCatalog: [ClaimedPermission]);
        builder.Services.AddIyuExternalSignIn();
        builder.Services.AddAuthentication().AddOpenIdConnect("oidc", o => { provider.Configure(o); handler?.Invoke(o); });
        if (registerStore)
        {
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton<ISessionEndStore, DistributedCacheSessionEndStore>();
        }
        if (addServices) builder.Services.AddIyuBackChannelLogout(configure);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuExternalSignIn();
        app.MapIyuBackChannelLogout();
        app.MapGet("/whoami", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(none)");

        await app.StartAsync();
        var http = app.GetTestServer().CreateClient();
        http.BaseAddress = new Uri("https://localhost");
        return new Host(app, http, provider);
    }

    /// <summary>Signs in through the provider and returns the session's cookies.</summary>
    private static async Task<string> ExternalSessionAsync(Host host)
    {
        using var challenge = await host.Http.GetAsync("/api/auth/external/oidc");
        using var back = await host.Provider.AnswerAsync(host.Http, challenge);
        using var complete = await host.Http.SendAsync(With(HttpMethod.Get, back.Headers.Location!.OriginalString, CookiesFrom(back)));
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        return CookiesFrom(complete);
    }

    /// <summary>The refusal a provider reads: 400, <c>invalid_request</c>, and a description.</summary>
    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "a refusal must not be cached");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
        return body.RootElement.GetProperty("error_description").GetString()!;
    }

    [Fact]
    public async Task The_provider_s_notice_ends_the_session_opened_from_the_session_it_names()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));

        // As providers in use send it: signed, an audience array, the logout event, iat, jti, sid — and no expiry.
        using var accepted = await host.NotifyAsync(host.Provider.LogoutToken());

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);                          // 200, not 204: the specification's success
        Assert.Empty(await accepted.Content.ReadAsByteArrayAsync());
        Assert.True(accepted.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_notice_for_a_session_nobody_here_holds_is_a_success_and_ends_no_one_else()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var accepted = await host.NotifyAsync(host.Provider.LogoutToken(c => c["sid"] = "someone-else-s-session"));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_token_with_an_expiry_is_held_to_it()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var expired = await host.NotifyAsync(host.Provider.LogoutToken(c =>
        {
            c["iat"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            c["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        }));
        Assert.Contains("expired", await RefusalAsync(expired));
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));

        using var current = await host.NotifyAsync(host.Provider.LogoutToken(c => c["exp"] = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds()));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_token_without_an_expiry_is_accepted_only_while_it_is_young()
    {
        await using var host = await StartAsync(o => o.MaxAgeWithoutExpiration = TimeSpan.FromMinutes(1));
        var session = await ExternalSessionAsync(host);

        // Older than the window plus the handler's clock skew (five minutes by default).
        using var old = await host.NotifyAsync(host.Provider.LogoutToken(c => c["iat"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()));
        Assert.Contains("too long ago", await RefusalAsync(old));

        using var future = await host.NotifyAsync(host.Provider.LogoutToken(c => c["iat"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));
        Assert.Contains("future", await RefusalAsync(future));

        using var undated = await host.NotifyAsync(host.Provider.LogoutToken(c => c.Remove("iat")));
        Assert.Contains("iat", await RefusalAsync(undated));

        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task An_application_can_require_the_expiry_the_specification_requires()
    {
        await using var host = await StartAsync(o => o.RequireExpiration = true);
        var session = await ExternalSessionAsync(host);

        using var without = await host.NotifyAsync(host.Provider.LogoutToken());
        Assert.Contains("exp", await RefusalAsync(without));
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));

        using var with = await host.NotifyAsync(host.Provider.LogoutToken(c => c["exp"] = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds()));
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    [Fact]
    public async Task Only_the_provider_s_signature_makes_a_notice()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);
        using var rsa = RSA.Create(2048);

        using var forged = await host.NotifyAsync(host.Provider.LogoutToken(signedWith: new RsaSecurityKey(rsa) { KeyId = "someone-else" }));
        await RefusalAsync(forged);
        using var unsigned = await host.NotifyAsync(host.Provider.LogoutToken(unsigned: true));
        await RefusalAsync(unsigned);
        using var garbage = await host.NotifyAsync("not-a-token");
        await RefusalAsync(garbage);

        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_token_signed_with_an_algorithm_the_provider_does_not_sign_with_is_refused()
    {
        await using var host = await StartAsync();                  // the provider advertises RS256
        var session = await ExternalSessionAsync(host);

        using var other = await host.NotifyAsync(host.Provider.LogoutToken(algorithm: SecurityAlgorithms.RsaSha384));

        Assert.Contains("algorithm", await RefusalAsync(other));
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task The_algorithms_the_application_declares_for_the_provider_outrank_what_it_advertises()
    {
        // The provider advertises RS256 only; the application declares RS384 as well, for its identity tokens and so
        // for its logout tokens too.
        await using var host = await StartAsync(handler: o => o.TokenValidationParameters.ValidAlgorithms =
            [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384]);
        var session = await ExternalSessionAsync(host);

        using var undeclared = await host.NotifyAsync(host.Provider.LogoutToken(algorithm: SecurityAlgorithms.RsaSha512));
        await RefusalAsync(undeclared);
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));

        using var declared = await host.NotifyAsync(host.Provider.LogoutToken(algorithm: SecurityAlgorithms.RsaSha384));

        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_provider_that_advertises_no_algorithms_leaves_it_to_its_keys_rather_than_losing_every_notice()
    {
        // Refusing every notice would keep sessions alive that the provider ended — the unsafe side for a logout.
        await using var host = await StartAsync(handler: o => o.Configuration!.IdTokenSigningAlgValuesSupported.Clear());
        var session = await ExternalSessionAsync(host);

        using var notice = await host.NotifyAsync(host.Provider.LogoutToken(algorithm: SecurityAlgorithms.RsaSha384));

        Assert.Equal(HttpStatusCode.OK, notice.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_token_from_another_issuer_or_for_another_client_is_refused()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var otherIssuer = await host.NotifyAsync(host.Provider.LogoutToken(c => c["iss"] = "https://another-provider.test"));
        Assert.Contains("not issued by this provider", await RefusalAsync(otherIssuer));
        using var otherClient = await host.NotifyAsync(host.Provider.LogoutToken(c => c["aud"] = new[] { "another-app" }));
        Assert.Contains("not addressed to this client", await RefusalAsync(otherClient));

        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-an-object")]
    [InlineData("another-event")]
    [InlineData("event-not-an-object")]
    public async Task A_signed_token_that_does_not_name_the_logout_event_is_not_a_notice(string shape)
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var refused = await host.NotifyAsync(host.Provider.LogoutToken(c =>
        {
            switch (shape)
            {
                case "missing": c.Remove("events"); break;
                case "not-an-object": c["events"] = "http://schemas.openid.net/event/backchannel-logout"; break;
                case "another-event": c["events"] = new Dictionary<string, object> { ["http://schemas.example/other"] = new Dictionary<string, object>() }; break;
                default: c["events"] = new Dictionary<string, object> { ["http://schemas.openid.net/event/backchannel-logout"] = "yes" }; break;
            }
        }));

        Assert.Contains("events", await RefusalAsync(refused));
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    /// <summary>
    /// The provider's document as a configuration manager reads it: the keys listed when it was first read, and a key the
    /// provider rolled to once it is read again.
    /// </summary>
    private sealed class RollingKeys(OpenIdConnectConfiguration document, SecurityKey rolledTo)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private bool _readAgain;
        public int Refreshes { get; private set; }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            if (_readAgain && !document.SigningKeys.Contains(rolledTo)) document.SigningKeys.Add(rolledTo);
            return Task.FromResult(document);
        }

        public void RequestRefresh() { _readAgain = true; Refreshes++; }
    }

    private static async Task<(Host Host, RollingKeys Keys)> StartRollingAsync(SecurityKey rolledTo)
    {
        RollingKeys? keys = null;
        var host = await StartAsync(handler: o =>
        {
            keys = new RollingKeys(o.Configuration!, rolledTo);
            o.Configuration = null;
            o.ConfigurationManager = keys;
        });
        return (host, keys!);
    }

    [Fact]
    public async Task A_notice_signed_with_a_key_the_provider_rolled_to_is_accepted_once_its_keys_are_read_again()
    {
        var rolled = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "rolled-key" };
        var (host, keys) = await StartRollingAsync(rolled);
        await using var _ = host;
        var session = await ExternalSessionAsync(host);

        using var accepted = await host.NotifyAsync(host.Provider.LogoutToken(signedWith: rolled));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, keys.Refreshes);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_notice_signed_with_a_key_the_provider_never_listed_is_refused_after_one_more_read()
    {
        var (host, keys) = await StartRollingAsync(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "rolled-key" });
        await using var _ = host;
        var session = await ExternalSessionAsync(host);
        var stranger = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "stranger-key" };

        using var refused = await host.NotifyAsync(host.Provider.LogoutToken(signedWith: stranger));

        // Not «expired»: the token carries no expiry — the library words an unknown key that way for such a token.
        Assert.Equal("The logout token is not a token signed by this provider.", await RefusalAsync(refused));
        Assert.Equal(1, keys.Refreshes);
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task The_identity_token_the_provider_issued_cannot_be_posted_as_a_notice()
    {
        // It is signed by the same key, for the same client, and names the same session — and it is not a logout token.
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var refused = await host.NotifyAsync(host.Provider.LastIdToken!);

        await RefusalAsync(refused);
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_logout_token_that_carries_a_nonce_is_refused()
    {
        await using var host = await StartAsync();

        using var refused = await host.NotifyAsync(host.Provider.LogoutToken(c => c["nonce"] = "n"));

        Assert.Contains("nonce", await RefusalAsync(refused));
    }

    [Fact]
    public async Task A_token_that_names_a_person_and_no_session_is_refused_rather_than_half_honoured()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var refused = await host.NotifyAsync(host.Provider.LogoutToken(c => { c.Remove("sid"); c["sub"] = host.Provider.Subject; }));

        Assert.Contains("backchannel_logout_session_required", await RefusalAsync(refused));
        Assert.Equal(HttpStatusCode.OK, await host.SessionStatusAsync(session));
    }

    [Fact]
    public async Task A_request_that_is_not_the_provider_s_form_post_is_refused()
    {
        await using var host = await StartAsync();

        using var empty = await host.Http.PostAsync(Endpoint, new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Contains("logout_token", await RefusalAsync(empty));
        using var json = await host.Http.PostAsync(Endpoint, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Contains("form", await RefusalAsync(json));
    }

    [Theory]
    [InlineData("nobody")]                 // no such handler
    [InlineData("Cookies")]                // the session cookie is not a provider
    [InlineData("Iyu.External")]           // nor is the scheme that holds a provider's answer
    public async Task Only_an_OpenID_Connect_handler_receives_notices(string provider)
    {
        await using var host = await StartAsync();

        using var response = await host.NotifyAsync(host.Provider.LogoutToken(), $"/api/auth/external/{provider}/backchannel-logout");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_is_the_provider_s_to_call_it_asks_for_no_session()
    {
        // The fallback policy requires an authenticated user everywhere else; a provider has no session here.
        await using var host = await StartAsync();

        using var response = await host.NotifyAsync(host.Provider.LogoutToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Mapping_the_endpoint_without_the_services_does_not_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(addServices: false, registerStore: false));

        Assert.Contains(nameof(IyuBackChannelLogoutExtensions.AddIyuBackChannelLogout), error.Message);
    }

    [Fact]
    public async Task Turning_it_on_without_a_store_does_not_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(registerStore: false));

        Assert.Contains(nameof(ISessionEndStore), error.Message);
    }

    [Fact]
    public void A_window_that_accepts_nothing_is_refused_with_the_option_that_says_so()
    {
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddIyuBackChannelLogout(o => o.MaxAgeWithoutExpiration = TimeSpan.Zero));

        Assert.Contains(nameof(BackChannelLogoutOptions.RequireExpiration), error.Message);
    }
}
