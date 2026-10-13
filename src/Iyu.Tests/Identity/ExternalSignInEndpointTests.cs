using System.Net;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Iyu.Tests.Identity.OidcStandIn;

namespace Iyu.Tests.Identity;

/// <summary>
/// Sign-in through an external provider as the framework composes it (<c>AddIyuExternalSignIn</c> +
/// <c>MapIyuExternalSignIn</c>): the provider's answer is held in a scheme of its own, the application names the local
/// account, and the session is that account's — opened from the claims source, with nothing the provider sent.
/// </summary>
public class ExternalSignInEndpointTests
{
    private static readonly string LocalAccount = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString();

    /// <summary>Links one provider identity to <see cref="LocalAccount"/>; refuses everyone else. Records what it was asked.</summary>
    private sealed class LinkingResolver : IExternalLoginResolver
    {
        public ExternalLogin? Asked { get; private set; }
        public string LinkedKey { get; set; } = "provider-subject-1";

        public Task<string?> ResolveSubjectAsync(ExternalLogin login, CancellationToken ct)
        {
            Asked = login;
            return Task.FromResult(login.Provider == "oidc" && login.ProviderKey == LinkedKey ? LocalAccount : null);
        }
    }

    private sealed record Host(WebApplication App, HttpClient Http, OidcStandIn Provider, LinkingResolver Resolver,
        FakeUserClaimsSource Accounts) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    /// <param name="registerResolver">Whether the application registers its <see cref="IExternalLoginResolver"/>.</param>
    /// <param name="revalidateEveryRequest">Ask the claims source again on every request (the session cookie is re-issued when its answer changed).</param>
    private static async Task<Host> StartAsync(bool registerResolver = true, bool revalidateEveryRequest = false)
    {
        var provider = new OidcStandIn();
        var resolver = new LinkingResolver();
        var accounts = new FakeUserClaimsSource();
        accounts.Set(LocalAccount);   // an account with no permission of its own

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        if (registerResolver) builder.Services.AddSingleton<IExternalLoginResolver>(resolver);
        var options = new IdentityTokenOptions { SigningKey = new string('k', 32) };
        if (revalidateEveryRequest) options.SessionRevalidationInterval = TimeSpan.Zero;
        builder.Services.AddIyuIdentity(options, permissionCatalog: [ClaimedPermission]);
        builder.Services.AddIyuExternalSignIn();
        // No SignInScheme: the framework's external scheme is where a remote handler signs in by default.
        builder.Services.AddAuthentication().AddOpenIdConnect("oidc", provider.Configure);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuExternalSignIn();
        app.MapGet("/whoami", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(none)");
        app.MapGet("/orders", () => "orders").RequireAuthorization(ClaimedPermission);
        // An application's own sign-out that names no scheme — what such endpoints commonly look like.
        app.MapPost("/my-sign-out", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return Results.Ok();
        });
        // A session the application opens itself, with no provider behind it (a local password check, say).
        app.MapPost("/local-sign-in", async (HttpContext http, UserSessionService sessions)
            => await sessions.SignInAsync(http, LocalAccount) ? Results.Ok() : Results.Unauthorized()).AllowAnonymous();

        await app.StartAsync();
        var http = app.GetTestServer().CreateClient();
        http.BaseAddress = new Uri("https://localhost");
        return new Host(app, http, provider, resolver, accounts);
    }

    /// <summary>Starts a sign-in, answers at the provider, and returns the handler's redirect to the completion endpoint.</summary>
    private static async Task<HttpResponseMessage> BackFromProviderAsync(Host host, string start)
    {
        using var challenge = await host.Http.GetAsync(start);
        return await host.Provider.AnswerAsync(host.Http, challenge);
    }

    private static Task<HttpResponseMessage> FollowAsync(Host host, HttpResponseMessage redirect)
        => host.Http.SendAsync(With(HttpMethod.Get, redirect.Headers.Location!.OriginalString, CookiesFrom(redirect)));

    [Fact]
    public async Task A_linked_external_identity_opens_the_local_account_s_session_and_returns_where_it_started()
    {
        await using var host = await StartAsync();

        using var back = await BackFromProviderAsync(host, "/api/auth/external/oidc?returnUrl=%2Freports%3Fy%3D2026");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/api/auth/external/complete?returnUrl=%2Freports%3Fy%3D2026", back.Headers.Location!.OriginalString);

        // Holding only the provider's answer is not being signed in.
        using var before = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", CookiesFrom(back)));
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);

        using var complete = await FollowAsync(host, back);
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        Assert.Equal("/reports?y=2026", complete.Headers.Location!.OriginalString);
        Assert.Contains(".Iyu.External", CookiesDeletedBy(complete));                 // the provider's answer is discarded

        // The resolver was asked with the provider and its identifier for the person.
        Assert.Equal("oidc", host.Resolver.Asked!.Provider);
        Assert.Equal(host.Provider.Subject, host.Resolver.Asked.ProviderKey);

        var session = CookiesFrom(complete);
        using var whoami = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", session));
        Assert.Equal(LocalAccount, await whoami.Content.ReadAsStringAsync());

        // The permission the provider's token named was not granted here, so the session does not hold it.
        using var orders = await host.Http.SendAsync(With(HttpMethod.Get, "/orders", session));
        Assert.Equal(HttpStatusCode.Forbidden, orders.StatusCode);
    }

    [Fact]
    public async Task The_session_carries_what_the_claims_source_grants_the_account()
    {
        await using var host = await StartAsync();
        host.Accounts.Set(LocalAccount, ClaimedPermission);

        using var back = await BackFromProviderAsync(host, "/api/auth/external/oidc");
        using var complete = await FollowAsync(host, back);
        Assert.Equal("/", complete.Headers.Location!.OriginalString);                 // no returnUrl: the application root

        using var orders = await host.Http.SendAsync(With(HttpMethod.Get, "/orders", CookiesFrom(complete)));
        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);
    }

    [Fact]
    public async Task An_identity_the_application_does_not_link_is_not_signed_in()
    {
        await using var host = await StartAsync();
        host.Resolver.LinkedKey = "someone-else";

        using var back = await BackFromProviderAsync(host, "/api/auth/external/oidc");
        using var complete = await FollowAsync(host, back);

        Assert.Equal(HttpStatusCode.Forbidden, complete.StatusCode);
        Assert.DoesNotContain(".AspNetCore.Cookies", CookiesFrom(complete));
        Assert.Contains(".Iyu.External", CookiesDeletedBy(complete));
    }

    [Fact]
    public async Task An_account_the_claims_source_refuses_is_not_signed_in()
    {
        await using var host = await StartAsync();
        host.Accounts.Set(LocalAccount, null);                                        // deactivated

        using var back = await BackFromProviderAsync(host, "/api/auth/external/oidc");
        using var complete = await FollowAsync(host, back);

        Assert.Equal(HttpStatusCode.Forbidden, complete.StatusCode);
        Assert.DoesNotContain(".AspNetCore.Cookies", CookiesFrom(complete));
    }

    [Fact]
    public async Task Completing_without_having_been_to_the_provider_is_401()
    {
        await using var host = await StartAsync();

        using var complete = await host.Http.GetAsync("/api/auth/external/complete");

        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
        Assert.Null(host.Resolver.Asked);
    }

    [Theory]
    [InlineData("https://elsewhere.test/")]
    [InlineData("//elsewhere.test/")]
    [InlineData("/\\elsewhere.test/")]
    public async Task A_return_address_off_the_application_is_refused_at_both_ends(string returnUrl)
    {
        await using var host = await StartAsync();
        var escaped = Uri.EscapeDataString(returnUrl);

        using var start = await host.Http.GetAsync($"/api/auth/external/oidc?returnUrl={escaped}");
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);

        // Crafted straight onto the completion endpoint, after a genuine trip to the provider.
        using var back = await BackFromProviderAsync(host, "/api/auth/external/oidc");
        using var complete = await host.Http.SendAsync(
            With(HttpMethod.Get, $"/api/auth/external/complete?returnUrl={escaped}", CookiesFrom(back)));
        Assert.Equal(HttpStatusCode.BadRequest, complete.StatusCode);
        Assert.DoesNotContain(".AspNetCore.Cookies", CookiesFrom(complete));
    }

    [Theory]
    [InlineData("nobody")]                                                           // no such scheme
    [InlineData("Cookies")]                                                          // the session cookie is not a provider
    [InlineData("Iyu.External")]
    public async Task Only_a_remote_handler_can_be_started(string provider)
    {
        await using var host = await StartAsync();

        using var start = await host.Http.GetAsync($"/api/auth/external/{provider}");

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    /// <summary>
    /// The external scheme is the default for signing in; signing out must not follow it there. An application's
    /// sign-out that names no scheme still ends the session, as it did before external sign-in was turned on.
    /// </summary>
    [Fact]
    public async Task A_sign_out_that_names_no_scheme_still_ends_the_session()
    {
        await using var host = await StartAsync();
        using var back = await BackFromProviderAsync(host, "/api/auth/external/oidc");
        using var complete = await FollowAsync(host, back);
        var session = CookiesFrom(complete);

        using var signOut = await host.Http.SendAsync(With(HttpMethod.Post, "/my-sign-out", session));
        Assert.Contains(".AspNetCore.Cookies", CookiesDeletedBy(signOut));
    }

    /// <summary>Signs in through the provider and returns the session's cookies.</summary>
    private static async Task<string> ExternalSessionAsync(Host host)
    {
        using var back = await BackFromProviderAsync(host, "/api/auth/external/oidc");
        using var complete = await FollowAsync(host, back);
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        return CookiesFrom(complete);
    }

    [Fact]
    public async Task Signing_out_of_an_external_session_ends_it_at_the_provider_too_and_comes_back()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var signOut = await host.Http.SendAsync(With(HttpMethod.Post, "/api/auth/sign-out?returnUrl=%2Fbye", session));

        Assert.Contains(".AspNetCore.Cookies", CookiesDeletedBy(signOut));
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        var toProvider = signOut.Headers.Location!;
        Assert.StartsWith(Issuer + "/end-session", toProvider.OriginalString);
        var query = System.Web.HttpUtility.ParseQueryString(toProvider.Query);
        Assert.Equal(host.Provider.LastIdToken, query["id_token_hint"]);              // the session named to the provider
        Assert.Equal("https://localhost/signout-callback-oidc", query["post_logout_redirect_uri"]);

        // The provider sends the browser back; the handler then returns it where the sign-out asked.
        using var callback = await host.Http.GetAsync($"/signout-callback-oidc?state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/bye", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_provider_is_remembered_across_a_re_issued_session_cookie()
    {
        await using var host = await StartAsync(revalidateEveryRequest: true);
        var session = await ExternalSessionAsync(host);

        // Asked again on this request, the claims source answers something new, and that re-issues the cookie.
        host.Accounts.Set(LocalAccount, ClaimedPermission);
        using var whoami = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", session));
        Assert.Equal(LocalAccount, await whoami.Content.ReadAsStringAsync());
        var reissued = CookiesFrom(whoami);
        Assert.Contains(".AspNetCore.Cookies", reissued);

        using var signOut = await host.Http.SendAsync(With(HttpMethod.Post, "/api/auth/sign-out", reissued));
        Assert.StartsWith(Issuer + "/end-session", signOut.Headers.Location!.OriginalString);
        Assert.Contains("id_token_hint=", signOut.Headers.Location.Query);
    }

    [Fact]
    public async Task Signing_out_of_a_session_with_no_provider_behind_it_ends_it_here()
    {
        await using var host = await StartAsync();
        using var signIn = await host.Http.PostAsync("/local-sign-in", null);
        var session = CookiesFrom(signIn);

        using var signOut = await host.Http.SendAsync(With(HttpMethod.Post, "/api/auth/sign-out?returnUrl=%2Fbye", session));

        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.Equal("/bye", signOut.Headers.Location!.OriginalString);
        Assert.Contains(".AspNetCore.Cookies", CookiesDeletedBy(signOut));
        using var whoami = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", ""));
        Assert.Equal(HttpStatusCode.Unauthorized, whoami.StatusCode);
    }

    [Fact]
    public async Task Sign_out_will_not_send_the_browser_off_the_application()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        using var signOut = await host.Http.SendAsync(
            With(HttpMethod.Post, "/api/auth/sign-out?returnUrl=https%3A%2F%2Felsewhere.test%2F", session));

        Assert.Equal(HttpStatusCode.BadRequest, signOut.StatusCode);
        Assert.DoesNotContain(".AspNetCore.Cookies", CookiesDeletedBy(signOut));     // nothing was signed out
    }

    [Fact]
    public async Task A_host_without_a_resolver_does_not_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(registerResolver: false));

        Assert.Contains(nameof(IExternalLoginResolver), error.Message);
    }
}
