using System.Net;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Iyu.Tests.Identity.OidcStandIn;

namespace Iyu.Tests.Identity;

/// <summary>
/// A session opened through an external provider ends when the provider's own session behind it does
/// (<c>AddIyuExternalSessionEnds</c>): the end is recorded, and the session cookie is refused the next time it is
/// presented — not at its next revalidation.
/// </summary>
public class ExternalSessionEndTests
{
    private static readonly string LocalAccount = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString();

    private sealed class Linking : IExternalLoginResolver
    {
        public Task<string?> ResolveSubjectAsync(ExternalLogin login, CancellationToken ct) => Task.FromResult<string?>(LocalAccount);
    }

    /// <summary>Keeps what it is told and counts how often it is asked.</summary>
    private sealed class RecordingStore : IExternalSessionEndStore
    {
        public Dictionary<ExternalSession, DateTimeOffset> Ended { get; } = [];
        public int Asked { get; private set; }

        public Task RecordEndedAsync(ExternalSession session, DateTimeOffset keepUntil, CancellationToken ct)
        {
            Ended[session] = keepUntil;
            return Task.CompletedTask;
        }

        /// <summary>The store cannot be read — its cache is down, say.</summary>
        public bool Failing { get; set; }

        public Task<bool> HasEndedAsync(ExternalSession session, CancellationToken ct)
        {
            if (Failing) throw new InvalidOperationException("the store cannot be read");
            Asked++;
            return Task.FromResult(Ended.ContainsKey(session));
        }
    }

    private sealed record Host(WebApplication App, HttpClient Http, OidcStandIn Provider, RecordingStore Store,
        FakeUserClaimsSource Accounts, FakeTimeProvider Clock) : IAsyncDisposable
    {
        public async Task EndAsync(string provider, string sessionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ExternalSessionService>().EndAsync(provider, sessionId);
        }

        public async Task<HttpStatusCode> StatusAsync(string session)
        {
            using var response = await Http.SendAsync(With(HttpMethod.Get, "/whoami", session));
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    /// <param name="surface">Whether the application calls <c>AddIyuExternalSessionEnds</c>.</param>
    /// <param name="store">Whether it registers an <see cref="IExternalSessionEndStore"/>.</param>
    /// <param name="revalidateEveryRequest">Ask the claims source again on every request.</param>
    private static async Task<Host> StartAsync(bool surface = true, bool store = true, bool revalidateEveryRequest = false)
    {
        var provider = new OidcStandIn();
        var recording = new RecordingStore();
        var accounts = new FakeUserClaimsSource();
        accounts.Set(LocalAccount);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // The provider stand-in dates its tokens by the real clock, so the fixed clock starts there.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        builder.Services.AddSingleton<IExternalLoginResolver>(new Linking());
        var options = new IdentityTokenOptions { SigningKey = new string('k', 32) };
        if (revalidateEveryRequest) options.SessionRevalidationInterval = TimeSpan.Zero;
        builder.Services.AddIyuIdentity(options, permissionCatalog: [ClaimedPermission]);
        builder.Services.AddIyuExternalSignIn();
        if (surface) builder.Services.AddIyuExternalSessionEnds();
        if (store) builder.Services.AddSingleton<IExternalSessionEndStore>(recording);
        builder.Services.AddAuthentication().AddOpenIdConnect("oidc", provider.Configure);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuExternalSignIn();
        app.MapGet("/whoami", (ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(none)");
        app.MapPost("/local-sign-in", async (HttpContext http, UserSessionService sessions)
            => await sessions.SignInAsync(http, LocalAccount) ? Results.Ok() : Results.Unauthorized()).AllowAnonymous();

        await app.StartAsync();
        var http = app.GetTestServer().CreateClient();
        http.BaseAddress = new Uri("https://localhost");
        return new Host(app, http, provider, recording, accounts, clock);
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

    [Fact]
    public async Task A_session_ends_on_the_next_request_once_the_provider_s_session_behind_it_has_ended()
    {
        // The default interval: the claims source is not due to be asked again for minutes. The end does not wait for it.
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(session));

        await host.EndAsync("oidc", host.Provider.SessionId!);

        using var ended = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", session));
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        Assert.Contains(".AspNetCore.Cookies", CookiesDeletedBy(ended));

        // Signing in again at the provider opens a new session there, and a new one here.
        host.Provider.SessionId = "provider-session-2";
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(await ExternalSessionAsync(host)));
    }

    [Theory]
    [InlineData("oidc", "another-session")]             // someone else's session at the same provider
    [InlineData("another-provider", "provider-session-1")]   // the same identifier, handed out by another provider
    public async Task Ending_another_external_session_leaves_this_one_alone(string provider, string sessionId)
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        await host.EndAsync(provider, sessionId);

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(session));
    }

    [Fact]
    public async Task Ending_a_session_nobody_holds_or_one_already_ended_is_not_an_error()
    {
        await using var host = await StartAsync();

        await host.EndAsync("oidc", "never-seen");
        await host.EndAsync("oidc", "never-seen");

        Assert.Single(host.Store.Ended);
    }

    [Fact]
    public async Task The_external_session_is_remembered_across_a_re_issued_session_cookie()
    {
        await using var host = await StartAsync(revalidateEveryRequest: true);
        var session = await ExternalSessionAsync(host);

        // Asked again on this request, the claims source answers something new, and that re-issues the cookie.
        host.Accounts.Set(LocalAccount, ClaimedPermission);
        using var whoami = await host.Http.SendAsync(With(HttpMethod.Get, "/whoami", session));
        var reissued = CookiesFrom(whoami);
        Assert.Contains(".AspNetCore.Cookies", reissued);

        await host.EndAsync("oidc", host.Provider.SessionId!);

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(reissued));
    }

    [Fact]
    public async Task A_session_with_no_provider_behind_it_is_never_asked_about()
    {
        await using var host = await StartAsync();
        using var signIn = await host.Http.PostAsync("/local-sign-in", null);
        var session = CookiesFrom(signIn);

        await host.EndAsync("oidc", host.Provider.SessionId!);

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(session));
        Assert.Equal(0, host.Store.Asked);
    }

    [Fact]
    public async Task A_provider_that_names_no_session_opens_one_that_is_not_asked_about()
    {
        await using var host = await StartAsync();
        host.Provider.SessionId = null;
        var session = await ExternalSessionAsync(host);

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(session));
        Assert.Equal(0, host.Store.Asked);
    }

    [Fact]
    public async Task Every_request_of_an_external_session_asks_and_no_other_does()
    {
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(session));
        using var anonymous = await host.Http.GetAsync("/api/auth/external/complete");

        Assert.Equal(3, host.Store.Asked);
    }

    [Fact]
    public async Task A_store_that_cannot_be_read_fails_the_request_rather_than_letting_the_session_through()
    {
        // Whether the session has ended is not known, so it is not treated as one that has not.
        await using var host = await StartAsync();
        var session = await ExternalSessionAsync(host);
        host.Store.Failing = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StatusAsync(session));

        host.Store.Failing = false;
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(session));
    }

    [Fact]
    public async Task The_end_is_kept_for_as_long_as_a_session_cookie_could_still_be_presented()
    {
        await using var host = await StartAsync();
        var lifetime = host.App.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme).ExpireTimeSpan;

        await host.EndAsync("oidc", "provider-session-1");

        Assert.Equal(host.Clock.GetUtcNow() + lifetime, host.Store.Ended[new ExternalSession("oidc", "provider-session-1")]);
    }

    [Fact]
    public async Task Without_the_call_an_external_session_is_as_it_was()
    {
        // The session still records the provider's session; nothing asks about it.
        await using var host = await StartAsync(surface: false, store: false);
        var session = await ExternalSessionAsync(host);

        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(session));
        Assert.Equal(0, host.Store.Asked);
    }

    [Fact]
    public async Task A_host_that_asks_for_ends_without_a_store_does_not_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(surface: true, store: false));

        Assert.Contains(nameof(IExternalSessionEndStore), error.Message);
        Assert.Contains(nameof(DistributedCacheExternalSessionEndStore), error.Message);
    }

    [Fact]
    public async Task A_host_that_registers_a_store_without_asking_for_ends_does_not_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(surface: false, store: true));

        Assert.Contains(nameof(IExternalSessionEndStore), error.Message);
        Assert.Contains(nameof(IyuIdentityServiceCollectionExtensions.AddIyuExternalSessionEnds), error.Message);
    }

    // ---------- the store over IDistributedCache ----------

    private static IDistributedCache MemoryCache()
        => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task The_distributed_cache_store_answers_for_what_was_recorded_and_nothing_else()
    {
        var store = new DistributedCacheExternalSessionEndStore(MemoryCache());
        var session = new ExternalSession("oidc", "s1");

        Assert.False(await store.HasEndedAsync(session, default));
        await store.RecordEndedAsync(session, DateTimeOffset.UtcNow.AddHours(1), default);
        await store.RecordEndedAsync(session, DateTimeOffset.UtcNow.AddHours(1), default);      // again: not an error

        Assert.True(await store.HasEndedAsync(session, default));
        Assert.False(await store.HasEndedAsync(new ExternalSession("oidc", "s2"), default));
        Assert.False(await store.HasEndedAsync(new ExternalSession("other", "s1"), default));
    }

    [Fact]
    public async Task The_distributed_cache_store_keeps_two_pairs_apart_whatever_characters_they_hold()
    {
        var store = new DistributedCacheExternalSessionEndStore(MemoryCache());

        await store.RecordEndedAsync(new ExternalSession("a:b", "c"), DateTimeOffset.UtcNow.AddHours(1), default);

        Assert.False(await store.HasEndedAsync(new ExternalSession("a", "b:c"), default));
    }

    /// <summary>Records the options an entry was set with.</summary>
    private sealed class CapturingCache : IDistributedCache
    {
        public DistributedCacheEntryOptions? Options { get; private set; }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Options = options;
            return Task.CompletedTask;
        }

        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Options = options;
    }

    [Fact]
    public async Task The_distributed_cache_store_lets_the_cache_drop_the_entry_when_it_is_no_longer_needed()
    {
        var cache = new CapturingCache();
        var keepUntil = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

        await new DistributedCacheExternalSessionEndStore(cache).RecordEndedAsync(new ExternalSession("oidc", "s1"), keepUntil, default);

        Assert.Equal(keepUntil, cache.Options!.AbsoluteExpiration);
    }
}
