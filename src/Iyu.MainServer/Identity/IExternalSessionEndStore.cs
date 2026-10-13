namespace Iyu.MainServer.Identity;

/// <summary>
/// A person's session at an external provider — what a session opened through that provider came from.
/// </summary>
/// <param name="Provider">The authentication scheme of the provider's handler (<c>"oidc"</c>, say).</param>
/// <param name="SessionId">
/// The provider's identifier for the session — the <c>sid</c> of the identity token it issued. Unique only together
/// with <paramref name="Provider"/>.
/// </param>
public readonly record struct ExternalSession(string Provider, string SessionId);

/// <summary>
/// Remembers which external sessions have ended, so that the application's sessions opened from them end too.
/// Implemented by the consuming app, or with <see cref="DistributedCacheExternalSessionEndStore"/>; used once
/// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuExternalSessionEnds"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// A session cookie is held by the browser, not by the server, so "this session is over" cannot be done to it from
/// elsewhere — it can only be remembered here and noticed when the cookie is next presented.
/// <see cref="HasEndedAsync"/> is therefore asked on <b>every request</b> that presents a session opened through an
/// external provider: answer it from something that is cheap to read.
/// </para>
/// <para>
/// The record must be shared by every instance of the application — one kept in a single process ends the session on
/// that instance only.
/// </para>
/// </remarks>
public interface IExternalSessionEndStore
{
    /// <summary>Records that <paramref name="session"/> has ended. Recording one that already has is not an error.</summary>
    /// <param name="session">The external session that ended.</param>
    /// <param name="keepUntil">
    /// When the record may be dropped: past this, no session cookie opened from <paramref name="session"/> can still be
    /// alive.
    /// </param>
    /// <param name="ct">Cancels the write.</param>
    Task RecordEndedAsync(ExternalSession session, DateTimeOffset keepUntil, CancellationToken ct);

    /// <summary>Whether <paramref name="session"/> was recorded as ended.</summary>
    Task<bool> HasEndedAsync(ExternalSession session, CancellationToken ct);
}
