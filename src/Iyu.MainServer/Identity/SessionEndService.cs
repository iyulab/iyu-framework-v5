using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Ends sessions so that the cookie or token that names one is refused the next time it is presented
/// (<see cref="IyuIdentityServiceCollectionExtensions.AddIyuSessionEnds"/>).
/// </summary>
/// <remarks>
/// <para>
/// A session cookie and an access token are held by the client, not by the server: signing out removes the browser's
/// cookie, and revoking a refresh token stops the next refresh, but a copy of either kept elsewhere would still be
/// accepted until it expires. With this on, the end of a session is recorded (<see cref="ISessionEndStore"/>) and asked
/// about every time a session is presented — on that request, whatever
/// <see cref="IdentityTokenOptions.SessionRevalidationInterval"/> is.
/// </para>
/// <para>
/// Two kinds of session end here. <b>The application's own</b>: <see cref="UserSessionService.SignOutAsync"/> ends the
/// session cookie it is called with, <see cref="UserSessionService.SignInAsync"/> ends the one the request already
/// carried, and <see cref="UserTokenService.RevokeAsync"/> ends the sign-in a refresh token belongs to, its access
/// tokens included — each records it here without being asked to. <b>An external provider's</b>: a session opened
/// through a provider remembers the provider's session behind it (the <c>sid</c> of the identity token, when the provider
/// sent one), and <see cref="EndProviderSessionAsync"/> — called from wherever the application has verified the
/// provider's word, such as an OpenID Connect back-channel logout endpoint — ends every session opened from it.
/// </para>
/// <para>
/// A session that names no identifier — a cookie this framework did not open, an access token issued on its own by
/// <see cref="UserAccessTokenService"/> — cannot be ended one at a time; the claims source and the security stamp still
/// end it, as they do every session of the account.
/// </para>
/// </remarks>
public sealed class SessionEndService
{
    /// <summary>Where a session cookie records the identifier of the session this application opened.</summary>
    internal const string SessionIdKey = ".iyu.sid";

    /// <summary>Where a session opened through an external provider records the provider's session identifier.</summary>
    internal const string ProviderSessionIdKey = ".iyu.provider.sid";

    private readonly ISessionEndStore _store;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookies;
    private readonly IdentityTokenOptions _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<SessionEndService> _log;

    public SessionEndService(ISessionEndStore store, IOptionsMonitor<CookieAuthenticationOptions> cookies,
        IdentityTokenOptions tokens, TimeProvider clock, ILogger<SessionEndService> log)
    {
        _store = store; _cookies = cookies; _tokens = tokens; _clock = clock; _log = log;
    }

    /// <summary>
    /// Records that the person's session at <paramref name="provider"/> has ended. Every session of this application
    /// opened from it ends when its cookie is next presented.
    /// </summary>
    /// <param name="provider">The authentication scheme of the provider's handler.</param>
    /// <param name="sessionId">The provider's identifier for the session — the <c>sid</c> it named.</param>
    /// <param name="ct">Cancels the write to the store.</param>
    /// <remarks>
    /// Ending a session nobody here holds, or one already ended, is not an error: the outcome is the same.
    /// </remarks>
    public async Task EndProviderSessionAsync(string provider, string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // Two things can be opened from it: a session cookie, expired on its own once not presented for its lifetime, and
        // a sign-in on refresh tokens (UserTokenService.IssueAsync), whose newest token stays exchangeable for its own
        // lifetime. Either presented sooner is ended on sight; past the longer span from now, nothing is left to refuse.
        var keep = CookieLifetime > _tokens.RefreshTokenLifetime ? CookieLifetime : _tokens.RefreshTokenLifetime;
        await _store.RecordEndedAsync(new SessionKey(provider, sessionId), _clock.GetUtcNow() + keep, ct);
        _log.LogInformation("external session {SessionId} at {Provider} recorded as ended", sessionId, provider);
    }

    /// <summary>
    /// Records that the session cookie described by <paramref name="properties"/> has ended, when it names a session
    /// this application opened.
    /// </summary>
    internal async Task EndCookieSessionAsync(AuthenticationProperties properties, CancellationToken ct)
    {
        if (SessionOf(properties) is not { } session) return;

        // The cookie expires on its own within its lifetime of its last renewal, or at the expiry the application
        // opened it with, whichever is later — and it is not renewed again: it is refused when presented.
        var keepUntil = _clock.GetUtcNow() + CookieLifetime;
        if (properties.ExpiresUtc is { } expires && expires > keepUntil) keepUntil = expires;
        await _store.RecordEndedAsync(session, keepUntil, ct);
        _log.LogInformation("session {SessionId} recorded as ended", session.Id);
    }

    /// <summary>
    /// Records that the sign-in a refresh-token family belongs to has ended — the access tokens issued in it included,
    /// which live at most <paramref name="accessTokenLifetime"/> past now.
    /// </summary>
    internal async Task EndTokenSessionAsync(Guid familyId, TimeSpan accessTokenLifetime, CancellationToken ct)
    {
        await _store.RecordEndedAsync(TokenSession(familyId), _clock.GetUtcNow() + accessTokenLifetime, ct);
        _log.LogInformation("token session {SessionId} recorded as ended", familyId);
    }

    /// <summary>The session a refresh-token family is: its access tokens name it (<c>sid</c>).</summary>
    internal static SessionKey TokenSession(Guid familyId) => new(null, familyId.ToString("N"));

    /// <summary>Whether <paramref name="session"/> was recorded as ended.</summary>
    internal Task<bool> HasEndedAsync(SessionKey session, CancellationToken ct) => _store.HasEndedAsync(session, ct);

    /// <summary>
    /// Whether the session an access token names has ended. A token that names none (<c>sid</c>) has not.
    /// </summary>
    internal async Task<bool> HasTokenSessionEndedAsync(string? sessionId, CancellationToken ct)
        => !string.IsNullOrEmpty(sessionId) && await _store.HasEndedAsync(new SessionKey(null, sessionId), ct);

    /// <summary>
    /// Ends the session being validated when it — or the external session it was opened from — has ended.
    /// </summary>
    /// <returns><c>true</c> when the session was ended — nothing further is to be validated.</returns>
    internal async Task<bool> EndIfEndedAsync(CookieValidatePrincipalContext context)
    {
        var ct = context.HttpContext.RequestAborted;
        string? reason = null;
        if (SessionOf(context.Properties) is { } own && await _store.HasEndedAsync(own, ct))
            reason = "it was signed out";
        else if (ProviderSessionOf(context.Properties) is { } external && await _store.HasEndedAsync(external, ct))
            reason = $"its session at {external.Provider} has ended";
        if (reason is null) return false;

        _log.LogInformation("session ended for {Subject}: {Reason}",
            context.Principal is null ? "(no identifier)" : IyuIdentityClaims.FindSubject(context.Principal) ?? "(no identifier)",
            reason);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return true;
    }

    /// <summary>Whether a session cookie names any session that could have been ended — decided from the cookie alone.</summary>
    internal static bool NamesASession(AuthenticationProperties properties)
        => SessionOf(properties) is not null || ProviderSessionOf(properties) is not null;

    /// <summary>The session this application opened that a session cookie names, or <c>null</c>.</summary>
    internal static SessionKey? SessionOf(AuthenticationProperties properties)
        => properties.Items.TryGetValue(SessionIdKey, out var id) && !string.IsNullOrEmpty(id) ? new SessionKey(null, id) : null;

    /// <summary>The external session a session cookie was opened from, or <c>null</c> when it names none.</summary>
    internal static SessionKey? ProviderSessionOf(AuthenticationProperties properties)
        => properties.Items.TryGetValue(UserSessionService.ProviderKey, out var provider) && !string.IsNullOrEmpty(provider)
           && properties.Items.TryGetValue(ProviderSessionIdKey, out var sessionId) && !string.IsNullOrEmpty(sessionId)
            ? new SessionKey(provider, sessionId)
            : null;

    private TimeSpan CookieLifetime => _cookies.Get(CookieAuthenticationDefaults.AuthenticationScheme).ExpireTimeSpan;
}
