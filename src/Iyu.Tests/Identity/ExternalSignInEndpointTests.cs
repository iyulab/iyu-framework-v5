using System.Net;
using System.Security.Claims;
using Iyu.MainServer.Identity;
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

    private static async Task<Host> StartAsync(bool registerResolver = true)
    {
        var provider = new OidcStandIn();
        var resolver = new LinkingResolver();
        var accounts = new FakeUserClaimsSource();
        accounts.Set(LocalAccount);   // an account with no permission of its own

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        if (registerResolver) builder.Services.AddSingleton<IExternalLoginResolver>(resolver);
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = new string('k', 32) },
            permissionCatalog: [ClaimedPermission]);
        builder.Services.AddIyuExternalSignIn();
        // No SignInScheme: the framework's external scheme is where a remote handler signs in by default.
        builder.Services.AddAuthentication().AddOpenIdConnect("oidc", provider.Configure);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuExternalSignIn();
        app.MapGet("/whoami", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(none)");
        app.MapGet("/orders", () => "orders").RequireAuthorization(ClaimedPermission);

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

    [Fact]
    public async Task A_host_without_a_resolver_does_not_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(registerResolver: false));

        Assert.Contains(nameof(IExternalLoginResolver), error.Message);
    }
}
