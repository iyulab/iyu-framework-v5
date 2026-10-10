using System.Net;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// A session cookie is re-read from the application's claims source while it lives, so what changes about
/// the account — deactivated, a permission withdrawn — reaches the cookie as it reaches an access token at
/// its next refresh.
/// </summary>
public class UserSessionRevalidationTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>A source whose answers a test changes, and which counts how often it is asked.</summary>
    private sealed class CountingClaimsSource : IUserClaimsSource
    {
        private readonly Dictionary<string, string[]?> _permissions = new();

        public int Asked { get; private set; }

        public void Set(string subject, params string[]? permissions) => _permissions[subject] = permissions;

        public Task<IReadOnlyCollection<Claim>?> GetClaimsAsync(string subject, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult<IReadOnlyCollection<Claim>?>(
                _permissions.GetValueOrDefault(subject)?.Select(p => IyuIdentityClaims.Permission(p)).ToArray());
        }
    }

    private sealed record Host(WebApplication App, HttpClient Http, CountingClaimsSource Claims, FakeTimeProvider Clock)
        : IAsyncDisposable
    {
        /// <summary>The cookies this client holds, kept up to date from every response like a browser's.</summary>
        public string Cookies { get; private set; } = "";

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            if (Cookies.Length > 0) request.Headers.Add("Cookie", Cookies);
            var response = await Http.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                Cookies = string.Join("; ", set.Select(v => v.Split(';', 2)[0]).Where(pair => !pair.EndsWith('=')));
            return response;
        }

        public async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path) => (await SendAsync(method, path)).StatusCode;

        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Host> StartAsync(Action<IdentityTokenOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var store = new FakeIdentityStore();
        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<IServiceClientStore>(store);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
        builder.Services.AddSingleton<TimeProvider>(clock);
        var claims = new CountingClaimsSource();
        builder.Services.AddSingleton<IUserClaimsSource>(claims);
        var options = new IdentityTokenOptions { SigningKey = Key, SessionRevalidationInterval = Interval };
        configure?.Invoke(options);
        builder.Services.AddIyuIdentity(options, permissionCatalog: ["orders.read", "orders.write"]);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapPost("/sign-in/{subject}", async (string subject, HttpContext http, UserSessionService sessions)
            => await sessions.SignInAsync(http, subject) ? Results.Ok() : Results.Unauthorized()).AllowAnonymous();

        // What a sign-in endpoint written before the session service does: a principal built by hand. The
        // query says which identifier claim it writes (none, when absent) and which permission it grants itself.
        app.MapPost("/sign-in-by-hand/{subject}", async (string subject, string? identifierType, string? grant, HttpContext http) =>
        {
            var claims = new List<Claim>();
            if (identifierType is not null) claims.Add(new Claim(identifierType, subject));
            if (grant is not null) claims.Add(IyuIdentityClaims.Permission(grant));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok();
        }).AllowAnonymous();

        app.MapGet("/whoami", (ClaimsPrincipal user) => IyuIdentityClaims.FindSubject(user) ?? "(none)");
        app.MapGet("/orders", () => "orders").RequireAuthorization("orders.read");
        app.MapPost("/orders", () => "written").RequireAuthorization("orders.write");

        await app.StartAsync();
        return new Host(app, app.GetTestClient(), claims, clock);
    }

    [Fact]
    public async Task Inside_the_interval_the_cookie_is_the_answer_and_the_source_is_not_asked()
    {
        await using var host = await StartAsync();
        host.Claims.Set("u1", "orders.read", "orders.write");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");
        var askedAtSignIn = host.Claims.Asked;

        host.Claims.Set("u1", "orders.read");                         // withdrawn, but the interval has not passed
        host.Clock.Advance(Interval - TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Post, "/orders"));
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));
        Assert.Equal(askedAtSignIn, host.Claims.Asked);
    }

    [Fact]
    public async Task After_the_interval_a_withdrawn_permission_is_gone_and_the_renewed_cookie_carries_the_new_answer()
    {
        await using var host = await StartAsync();
        host.Claims.Set("u1", "orders.read", "orders.write");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");

        host.Claims.Set("u1", "orders.read");
        host.Clock.Advance(Interval);
        var cookiesBefore = host.Cookies;

        Assert.Equal(HttpStatusCode.Forbidden, await host.StatusAsync(HttpMethod.Post, "/orders"));   // still signed in, no longer permitted
        Assert.NotEqual(cookiesBefore, host.Cookies);                                                 // re-issued with the new claims
        var askedAfterRenewal = host.Claims.Asked;

        // The renewed cookie is good for the next interval: the same answer, without asking again.
        Assert.Equal(HttpStatusCode.Forbidden, await host.StatusAsync(HttpMethod.Post, "/orders"));
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));
        Assert.Equal(askedAfterRenewal, host.Claims.Asked);
    }

    [Fact]
    public async Task After_the_interval_a_person_the_source_now_refuses_is_signed_out()
    {
        await using var host = await StartAsync();
        host.Claims.Set("u1", "orders.read");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");

        host.Claims.Set("u1", null);                                  // deactivated
        host.Clock.Advance(Interval);

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(HttpMethod.Get, "/orders"));
        Assert.Equal("", host.Cookies);                               // the session cookie was taken back
    }

    [Fact]
    public async Task With_a_zero_interval_every_request_asks()
    {
        await using var host = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
        host.Claims.Set("u1", "orders.read", "orders.write");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");

        host.Claims.Set("u1", "orders.read");

        Assert.Equal(HttpStatusCode.Forbidden, await host.StatusAsync(HttpMethod.Post, "/orders"));
    }

    /// <summary>
    /// A cookie an endpoint built by hand is not exempt: the first time it is presented it becomes what the
    /// source answers for the account it names — so a permission the endpoint granted itself does not survive.
    /// </summary>
    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public async Task A_hand_built_session_is_reassembled_from_the_source_at_its_first_request(string identifierType)
    {
        await using var host = await StartAsync();
        host.Claims.Set("u1", "orders.read");
        await host.SendAsync(HttpMethod.Post,
            $"/sign-in-by-hand/u1?identifierType={Uri.EscapeDataString(identifierType)}&grant=orders.write");

        Assert.Equal(HttpStatusCode.Forbidden, await host.StatusAsync(HttpMethod.Post, "/orders"));   // self-granted: gone
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));           // the source's: there
        Assert.Equal("u1", await (await host.SendAsync(HttpMethod.Get, "/whoami")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_hand_built_session_that_names_nobody_is_signed_out()
    {
        await using var host = await StartAsync();
        await host.SendAsync(HttpMethod.Post, "/sign-in-by-hand/u1?grant=orders.read");

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(HttpMethod.Get, "/orders"));
        Assert.Equal(0, host.Claims.Asked);                           // there was no subject to ask about
    }

    /// <summary>
    /// Where the application has declared that an external provider's identity is its session, a cookie this
    /// service did not open is exactly that — there is no account to ask about, and it is left as it is.
    /// </summary>
    [Fact]
    public async Task Where_an_external_identity_is_the_session_a_cookie_the_service_did_not_open_is_left_alone()
    {
        await using var host = await StartAsync(o => o.AllowExternalIdentityAsSession = true);
        await host.SendAsync(HttpMethod.Post, "/sign-in-by-hand/someone-at-the-provider?identifierType=sub&grant=orders.read");
        host.Clock.Advance(Interval + Interval);

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));
        Assert.Equal(0, host.Claims.Asked);
    }

    [Fact]
    public void A_negative_interval_is_refused_when_identity_is_registered()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() => services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = Key, SessionRevalidationInterval = TimeSpan.FromSeconds(-1) },
            permissionCatalog: []));

        Assert.Contains(nameof(IdentityTokenOptions.SessionRevalidationInterval), error.Message);
    }
}
