namespace Iyu.MainServer.Identity;

/// <summary>
/// One session, as a record of its end names it: a session this application opened, or a person's session at an
/// external provider that a session of this application was opened from.
/// </summary>
/// <param name="Provider">
/// The authentication scheme of the provider's handler (<c>"oidc"</c>, say) for a provider's session; <c>null</c> for a
/// session this application opened itself.
/// </param>
/// <param name="Id">
/// The session's identifier — for a provider's session, the <c>sid</c> of the identity token it issued. Unique only
/// together with <paramref name="Provider"/>.
/// </param>
public readonly record struct SessionKey(string? Provider, string Id);

/// <summary>
/// Remembers which sessions have ended, so that a session cookie or an access token that still names one is refused.
/// Implemented by the consuming app, or with <see cref="DistributedCacheSessionEndStore"/>; used once
/// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuSessionEnds"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// A session cookie is held by the browser and an access token by the client, not by the server, so "this session is
/// over" cannot be done to either from elsewhere — it can only be remembered here and noticed when the session is next
/// presented. <see cref="HasEndedAsync"/> is therefore asked on <b>every request</b> that presents a session: answer it
/// from something that is cheap to read.
/// </para>
/// <para>
/// The record must be shared by every instance of the application — one kept in a single process ends the session on
/// that instance only.
/// </para>
/// </remarks>
public interface ISessionEndStore
{
    /// <summary>Records that <paramref name="session"/> has ended. Recording one that already has is not an error.</summary>
    /// <param name="session">The session that ended.</param>
    /// <param name="keepUntil">
    /// When the record may be dropped: past this, nothing that names <paramref name="session"/> can still be alive.
    /// </param>
    /// <param name="ct">Cancels the write.</param>
    Task RecordEndedAsync(SessionKey session, DateTimeOffset keepUntil, CancellationToken ct);

    /// <summary>Whether <paramref name="session"/> was recorded as ended.</summary>
    Task<bool> HasEndedAsync(SessionKey session, CancellationToken ct);
}
