using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Ends the application's sessions that were opened from an external provider's session, when that session ends
/// (<see cref="IyuIdentityServiceCollectionExtensions.AddIyuExternalSessionEnds"/>).
/// </summary>
/// <remarks>
/// <para>
/// A session opened through an external provider remembers the provider's own session behind it — the <c>sid</c> of
/// the identity token, when the provider sent one. When the provider says that session is over (the person signed out
/// of another application, or at the provider itself), <see cref="EndAsync"/> records it, and every session cookie
/// opened from it ends the next time it is presented — on that request, whatever
/// <see cref="IdentityTokenOptions.SessionRevalidationInterval"/> is.
/// </para>
/// <para>
/// Who may say so is not decided here. Call this from wherever the application has verified the provider's word: an
/// OpenID Connect back-channel logout endpoint, or a channel of the application's own.
/// </para>
/// <para>
/// A session opened without the provider's session identifier (the provider sent no <c>sid</c>) is never ended this
/// way, and a session opened by any other means is not asked about at all.
/// </para>
/// </remarks>
public sealed class ExternalSessionService
{
    /// <summary>Where a session opened through an external provider records the provider's session identifier.</summary>
    internal const string SessionIdKey = ".iyu.provider.sid";

    private readonly IExternalSessionEndStore _store;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookies;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExternalSessionService> _log;

    public ExternalSessionService(IExternalSessionEndStore store, IOptionsMonitor<CookieAuthenticationOptions> cookies,
        TimeProvider clock, ILogger<ExternalSessionService> log)
    {
        _store = store; _cookies = cookies; _clock = clock; _log = log;
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
    public async Task EndAsync(string provider, string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // A cookie not presented for its whole lifetime has expired on its own; one presented sooner is ended on
        // sight. Past that span from now, nothing opened from this session is left to refuse.
        var lifetime = _cookies.Get(CookieAuthenticationDefaults.AuthenticationScheme).ExpireTimeSpan;
        await _store.RecordEndedAsync(new ExternalSession(provider, sessionId), _clock.GetUtcNow() + lifetime, ct);
        _log.LogInformation("external session {SessionId} at {Provider} recorded as ended", sessionId, provider);
    }

    /// <summary>
    /// The external session a session cookie was opened from, or <c>null</c> when it names none — decided from the
    /// cookie alone, before anything is resolved.
    /// </summary>
    internal static ExternalSession? SessionOf(AuthenticationProperties properties)
        => properties.Items.TryGetValue(UserSessionService.ProviderKey, out var provider) && !string.IsNullOrEmpty(provider)
           && properties.Items.TryGetValue(SessionIdKey, out var sessionId) && !string.IsNullOrEmpty(sessionId)
            ? new ExternalSession(provider, sessionId)
            : null;

    /// <summary>
    /// Ends the session being validated when the external session behind it has ended.
    /// </summary>
    /// <returns><c>true</c> when the session was ended — nothing further is to be validated.</returns>
    internal async Task<bool> EndIfEndedAsync(CookieValidatePrincipalContext context, ExternalSession session)
    {
        if (!await _store.HasEndedAsync(session, context.HttpContext.RequestAborted)) return false;

        _log.LogInformation("session ended for {Subject}: its session at {Provider} has ended",
            context.Principal is null ? "(no identifier)" : IyuIdentityClaims.FindSubject(context.Principal) ?? "(no identifier)",
            session.Provider);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return true;
    }
}
