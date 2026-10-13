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
        private readonly Dictionary<string, string> _stamps = new();

        public int Asked { get; private set; }

        public void Set(string subject, params string[]? permissions) => _permissions[subject] = permissions;

        /// <summary>The security stamp answered for the subject from now on — what a password change replaces.</summary>
        public void Stamp(string subject, string stamp) => _stamps[subject] = stamp;

        public Task<IReadOnlyCollection<Claim>?> GetClaimsAsync(string subject, CancellationToken ct)
        {
            Asked++;
            if (_permissions.GetValueOrDefault(subject) is not { } permissions)
                return Task.FromResult<IReadOnlyCollection<Claim>?>(null);

            var claims = permissions.Select(p => IyuIdentityClaims.Permission(p)).ToList();
            if (_stamps.TryGetValue(subject, out var stamp)) claims.Add(IyuIdentityClaims.SecurityStamp(stamp));
            return Task.FromResult<IReadOnlyCollection<Claim>?>(claims);
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

    /// <param name="configure">Identity options on top of the defaults here.</param>
    /// <param name="cookie">The session cookie's own options, applied after the framework's.</param>
    private static async Task<Host> StartAsync(Action<IdentityTokenOptions>? configure = null,
        Action<CookieAuthenticationOptions>? cookie = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
        builder.Services.AddSingleton<TimeProvider>(clock);
        var claims = new CountingClaimsSource();
        builder.Services.AddSingleton<IUserClaimsSource>(claims);
        var options = new IdentityTokenOptions { SigningKey = Key, SessionRevalidationInterval = Interval };
        configure?.Invoke(options);
        builder.Services.AddIyuIdentity(options, permissionCatalog: ["orders.read", "orders.write"]);
        if (cookie is not null)
            builder.Services.PostConfigure(CookieAuthenticationDefaults.AuthenticationScheme, cookie);

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
    /// The session belongs to the state of the account it was opened in. A new stamp — the password was changed —
    /// ends every session opened under the old one at its next revalidation, while one opened after the change
    /// goes on. The comparison happens before the claims are replaced; made after, it would always pass.
    /// </summary>
    [Fact]
    public async Task A_changed_security_stamp_ends_the_sessions_opened_under_the_old_one()
    {
        await using var host = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
        host.Claims.Set("u1", "orders.read");
        host.Claims.Stamp("u1", "stamp-1");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");
        var before = host.Cookies;
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));

        host.Claims.Stamp("u1", "stamp-2");                              // the password was changed elsewhere

        var ended = await host.SendAsync(HttpMethod.Get, "/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        Assert.Contains(ended.Headers.GetValues("Set-Cookie"), v => v.Contains("expires=Thu, 01 Jan 1970"));

        // The browser that changed it signs in again and holds a session opened under the new stamp.
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");
        Assert.NotEqual(before, host.Cookies);
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));
    }

    /// <summary>The same with an interval: the old session lasts until it is next due, and ends there.</summary>
    [Fact]
    public async Task With_an_interval_a_changed_stamp_ends_the_session_when_it_is_next_due()
    {
        await using var host = await StartAsync();
        host.Claims.Set("u1", "orders.read");
        host.Claims.Stamp("u1", "stamp-1");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");

        host.Claims.Stamp("u1", "stamp-2");
        host.Clock.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(HttpMethod.Get, "/orders"));
    }

    /// <summary>
    /// A session with no stamp cannot be compared with an account that now has one: a cookie opened before the
    /// source answered stamps, or one built by hand, ends rather than quietly acquiring the current stamp.
    /// </summary>
    [Fact]
    public async Task A_session_opened_without_a_stamp_ends_once_the_source_answers_one()
    {
        await using var host = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
        host.Claims.Set("u1", "orders.read");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));

        host.Claims.Stamp("u1", "stamp-1");

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(HttpMethod.Get, "/orders"));
    }

    /// <summary>
    /// Asking on every request does not mean writing on every request: while the source answers what the session
    /// already carries, the cookie is not re-issued — on a protected endpoint or an anonymous one.
    /// </summary>
    [Fact]
    public async Task With_a_zero_interval_an_unchanged_answer_does_not_re_issue_the_cookie()
    {
        await using var host = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
        host.Claims.Set("u1", "orders.read", "orders.write");
        host.Claims.Stamp("u1", "stamp-1");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");
        var asked = host.Claims.Asked;

        for (var i = 0; i < 5; i++)
        {
            var response = await host.SendAsync(HttpMethod.Get, i % 2 == 0 ? "/orders" : "/whoami");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"), "an unchanged session was re-issued");
        }
        Assert.Equal(asked + 5, host.Claims.Asked);                      // every request still asked

        // A change is written — once — and the next unchanged answer is quiet again.
        host.Claims.Set("u1", "orders.read");
        var changed = await host.SendAsync(HttpMethod.Get, "/orders");
        Assert.True(changed.Headers.Contains("Set-Cookie"), "a changed answer was not written to the cookie");
        Assert.Equal(HttpStatusCode.Forbidden, await host.StatusAsync(HttpMethod.Post, "/orders"));
        Assert.False((await host.SendAsync(HttpMethod.Get, "/orders")).Headers.Contains("Set-Cookie"));
    }

    /// <summary>
    /// With an interval the cookie records when the source last answered, so an unchanged answer is still written
    /// once per interval — and not again inside it.
    /// </summary>
    [Fact]
    public async Task With_an_interval_an_unchanged_answer_is_written_once_per_interval()
    {
        await using var host = await StartAsync();
        host.Claims.Set("u1", "orders.read");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");

        host.Clock.Advance(Interval + TimeSpan.FromSeconds(1));
        Assert.True((await host.SendAsync(HttpMethod.Get, "/orders")).Headers.Contains("Set-Cookie"));
        var asked = host.Claims.Asked;

        Assert.False((await host.SendAsync(HttpMethod.Get, "/orders")).Headers.Contains("Set-Cookie"));
        Assert.Equal(asked, host.Claims.Asked);
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

    /// <summary>
    /// Without sliding expiration a session ends when the sign-in said it would. Asking the source again re-issues
    /// the cookie, and re-issuing must not move that end — a session kept alive by its own revalidation would never end.
    /// </summary>
    [Fact]
    public async Task Without_sliding_expiration_a_re_issued_cookie_still_ends_when_the_sign_in_said()
    {
        await using var host = await StartAsync(cookie: o =>
        {
            o.SlidingExpiration = false;
            o.ExpireTimeSpan = TimeSpan.FromHours(1);
        });
        host.Claims.Set("u1", "orders.read");
        await host.SendAsync(HttpMethod.Post, "/sign-in/u1");
        var askedAtSignIn = host.Claims.Asked;

        // Fifty minutes in, past the interval: the source is asked and the cookie re-issued.
        host.Clock.Advance(TimeSpan.FromMinutes(50));
        using (var renewed = await host.SendAsync(HttpMethod.Get, "/orders"))
        {
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            Assert.True(renewed.Headers.Contains("Set-Cookie"), "the revalidated cookie was not re-issued");
        }
        Assert.True(host.Claims.Asked > askedAtSignIn);

        // Still inside the hour the sign-in gave it — and re-issued once more on the way.
        host.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(HttpMethod.Get, "/orders"));

        // The hour is over: the re-issued cookies did not carry the session past it.
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(HttpMethod.Get, "/orders"));
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
