using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// Signing a person in through an external OpenID Connect provider, as the README's recipe composes it on top of
/// <c>AddIyuIdentity</c>: what the provider says about the person must not become the application's session by itself.
/// </summary>
/// <remarks>
/// <para>
/// An OpenID Connect handler persists the identity it received into its <c>SignInScheme</c>, and when none is named
/// that is the application's default scheme — here, the session cookie every authorization policy reads. The provider's
/// claims would then be the session: a person with no local account passes "is signed in", and a claim the provider
/// (or the person, where the provider lets people edit their own claims) names like a permission passes the permission
/// policies. The recipe therefore receives the external identity in a scheme of its own, and the application opens its
/// session only after it has resolved a local account — and a host that leaves the handler on its default does not start.
/// </para>
/// <para>
/// The provider here is a stand-in: a fixed configuration and a token endpoint answered in process, signing an ID
/// token with a test key. Everything on the application's side is the real handler pipeline.
/// </para>
/// </remarks>
public class ExternalSignInIsolationTests
{
    private const string Issuer = "https://provider.test";
    private const string ClientId = "app";
    private const string ExternalScheme = "external";
    private const string ProviderSubject = "provider-subject-1";
    private static readonly Guid LocalAccount = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>Answers the code exchange with an ID token for <see cref="ProviderSubject"/> that also claims a permission.</summary>
    private sealed class ProviderTokenEndpoint(RsaSecurityKey key) : HttpMessageHandler
    {
        public string Nonce { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = ClientId,
                IssuedAt = DateTime.UtcNow,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddMinutes(5),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = ProviderSubject,
                    ["nonce"] = Nonce,
                    // What the provider (or the person, through it) asserts — in the application's own permission claim type.
                    [IyuIdentityClaims.DefaultPermissionClaimType] = "orders.read",
                },
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            });
            var json = $$"""{"access_token":"opaque","token_type":"Bearer","expires_in":300,"id_token":"{{idToken}}"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record Host(WebApplication App, HttpClient Http, ProviderTokenEndpoint Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    /// <param name="separateExternalScheme">
    /// <c>true</c>: the recipe — the handler signs the external identity into a scheme of its own.
    /// <c>false</c>: the handler's default, where no <c>SignInScheme</c> is named.
    /// </param>
    /// <param name="allowExternalIdentityAsSession">The application says the provider's identity is its session.</param>
    private static async Task<Host> StartAsync(bool separateExternalScheme, bool allowExternalIdentityAsSession = false)
    {
        var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" };
        var provider = new ProviderTokenEndpoint(key);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // The one local account, with no permission of its own — what the provider claims must not add one.
        var accounts = new FakeUserClaimsSource();
        accounts.Set(LocalAccount.ToString());
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        builder.Services.AddIyuIdentity(
            new IdentityTokenOptions
            {
                SigningKey = new string('k', 32),
                AllowExternalIdentityAsSession = allowExternalIdentityAsSession,
            },
            permissionCatalog: ["orders.read"]);

        builder.Services.AddAuthentication()
            .AddCookie(ExternalScheme)
            .AddOpenIdConnect("oidc", options =>
            {
                if (separateExternalScheme) options.SignInScheme = ExternalScheme;
                options.ClientId = ClientId;
                options.ClientSecret = "secret";
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    AuthorizationEndpoint = Issuer + "/authorize",
                    TokenEndpoint = Issuer + "/token",
                };
                options.Configuration.SigningKeys.Add(key);
                options.BackchannelHttpHandler = provider;
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/sign-in", () => Results.Challenge(
            new AuthenticationProperties { RedirectUri = "/sign-in/complete" }, ["oidc"])).AllowAnonymous();

        // The recipe's completion step: the application decides who the external identity is here, and only then
        // opens its own session — with claims it chose.
        app.MapGet("/sign-in/complete", async (HttpContext http, UserSessionService sessions) =>
        {
            var external = await http.AuthenticateAsync(ExternalScheme);
            if (!external.Succeeded) return Results.Unauthorized();

            var subject = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? external.Principal.FindFirstValue("sub");
            if (subject != ProviderSubject) return Results.Unauthorized();   // no local account for anyone else

            // The session is the local account's: its identifier and what the application's claims source answers.
            if (!await sessions.SignInAsync(http, LocalAccount.ToString())) return Results.Unauthorized();
            await http.SignOutAsync(ExternalScheme);
            return Results.Ok();
        }).AllowAnonymous();

        app.MapGet("/whoami", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(none)");
        app.MapGet("/orders", () => "orders").RequireAuthorization("orders.read");

        await app.StartAsync();
        var http = app.GetTestServer().CreateClient();
        http.BaseAddress = new Uri("https://localhost");
        return new Host(app, http, provider);
    }

    /// <summary>The cookies a response sets, as a request <c>Cookie</c> header value (deletions left out).</summary>
    private static string CookiesFrom(HttpResponseMessage response)
        => !response.Headers.TryGetValues("Set-Cookie", out var values)
            ? ""
            : string.Join("; ", values
                .Select(v => v.Split(';', 2)[0])
                .Where(pair => !pair.EndsWith('=')));

    private static HttpRequestMessage With(HttpMethod method, string path, string cookies, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (cookies.Length > 0) request.Headers.Add("Cookie", cookies);
        return request;
    }

    /// <summary>
    /// Runs the sign-in up to the point the provider's answer has been accepted, and returns the cookies the
    /// application set in response — without following the redirect to the completion step.
    /// </summary>
    private static async Task<string> SignInAtProviderAsync(Host host)
    {
        using var challenge = await host.Http.GetAsync("/sign-in");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        host.Provider.Nonce = query["nonce"]!;

        using var callback = await host.Http.SendAsync(With(HttpMethod.Post, "/signin-oidc", CookiesFrom(challenge),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "code", ["state"] = query["state"]! })));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/sign-in/complete", callback.Headers.Location!.OriginalString);
        return CookiesFrom(callback);
    }

    [Fact]
    public async Task What_the_provider_says_is_not_a_session_until_the_application_completes_the_sign_in()
    {
        await using var host = await StartAsync(separateExternalScheme: true);
        var afterProvider = await SignInAtProviderAsync(host);

        // The browser holds only the external identity. Nothing the application serves accepts it — not "is signed
        // in", and not the permission the provider's token named.
        using var whoami = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", afterProvider));
        using var orders = await host.Http.SendAsync(With(HttpMethod.Get, "/orders", afterProvider));
        Assert.Equal(HttpStatusCode.Unauthorized, whoami.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, orders.StatusCode);

        // The completion step resolves the local account and opens the application's session with its own claims.
        using var complete = await host.Http.SendAsync(With(HttpMethod.Get, "/sign-in/complete", afterProvider));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var session = CookiesFrom(complete);

        using var signedIn = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", session));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(LocalAccount.ToString(), await signedIn.Content.ReadAsStringAsync());

        // The permission the provider named was never granted by the application, so it is not held.
        using var stillNoOrders = await host.Http.SendAsync(With(HttpMethod.Get, "/orders", session));
        Assert.Equal(HttpStatusCode.Forbidden, stillNoOrders.StatusCode);
    }

    /// <summary>
    /// A handler left on its default would sign the external identity into the application's session cookie. That
    /// composition does not start: it reads like a working sign-in and gives the provider the last word on who is
    /// signed in and what they may do.
    /// </summary>
    [Fact]
    public async Task A_handler_that_would_sign_into_the_application_session_stops_the_host_from_starting()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(separateExternalScheme: false));

        Assert.Contains("'oidc'", error.Message);                                   // which handler
        Assert.Contains("SignInScheme", error.Message);                             // what to set
        Assert.Contains(nameof(IdentityTokenOptions.AllowExternalIdentityAsSession), error.Message);
    }

    /// <summary>
    /// What the refusal is about, shown with the opt-out on: the provider's subject is the signed-in user, and a
    /// permission only the provider named passes the permission policy.
    /// </summary>
    /// <remarks>
    /// The first half of this pins the platform's default rather than anything this package does — if it ever
    /// fails, a handler no longer falls back to the default scheme and the guard's reasoning should be read again.
    /// </remarks>
    [Fact]
    public async Task With_the_opt_out_the_external_identity_is_the_application_session()
    {
        await using var host = await StartAsync(separateExternalScheme: false, allowExternalIdentityAsSession: true);
        var afterProvider = await SignInAtProviderAsync(host);

        using var whoami = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", afterProvider));
        using var orders = await host.Http.SendAsync(With(HttpMethod.Get, "/orders", afterProvider));

        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        Assert.Equal(ProviderSubject, await whoami.Content.ReadAsStringAsync());   // the provider's subject, not a local account
        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);                        // a permission nobody here granted
    }
}
