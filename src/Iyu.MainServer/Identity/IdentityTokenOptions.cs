namespace Iyu.MainServer.Identity;

public sealed class IdentityTokenOptions
{
    public string SigningKey { get; set; } = default!;   // consumer-injected (>=32 bytes)
    public string Issuer { get; set; } = "iyu";
    public string Audience { get; set; } = "iyu-api";
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);
    public string PermissionClaimType { get; set; } = "perm";

    /// <summary>
    /// Whether a service-client access token is re-checked against its client on every request,
    /// so that revoking, rotating or re-scoping the client ends tokens already issued to it.
    /// </summary>
    /// <remarks>
    /// On by default. Turned off, those three operations only stop the <i>next</i> issuance: a
    /// token obtained before them stays valid for the rest of <see cref="Lifetime"/> — which is
    /// the window a leaked credential is revoked to close.
    /// </remarks>
    public bool ValidateServiceClientTokens { get; set; } = true;

    /// <summary>
    /// How long a successful check of a service-client token is reused before the store is asked
    /// again. This is the longest a revocation, rotation or permission change can take to reach a
    /// token that was in use when it happened. <see cref="TimeSpan.Zero"/> checks every request.
    /// </summary>
    public TimeSpan ServiceClientValidationWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a person's refresh token stays exchangeable (<see cref="UserTokenService"/>). Each
    /// exchange issues a new one with a fresh lifetime, so a client in regular use stays signed in
    /// and one left unused this long must sign in again.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long after a refresh token is exchanged the same token may be presented again without
    /// ending the sign-in (<see cref="UserTokenService"/>). <see cref="TimeSpan.Zero"/>, the default,
    /// is strict rotation: any second presentation revokes every token of the sign-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For clients on unreliable networks. A refresh that reaches the server and rotates the token
    /// but whose response is lost leaves the client holding only the old token; its retry is, to the
    /// server, a reuse, and under strict rotation it signs the person out, often exactly where the
    /// network is poor. Within this interval the retry is answered with a new pair instead, and the
    /// pair the lost response carried is retired, so the sign-in still has one live refresh token.
    /// </para>
    /// <para>
    /// The cost is detection: for this long after a legitimate refresh, whoever else holds a copy of
    /// the old token can exchange it as well (and the client that refreshed is then signed out at its
    /// next refresh, by reuse). Keep it to the seconds a retry takes. Outside the interval, and for a
    /// token whose replacement has already been used, a second presentation revokes the sign-in as
    /// before.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan RefreshTokenReuseInterval
    {
        get => _refreshTokenReuseInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _refreshTokenReuseInterval = value;
        }
    }

    private TimeSpan _refreshTokenReuseInterval = TimeSpan.Zero;

    /// <summary>
    /// The lifetime of a person's access token issued with a refresh token (<see cref="UserTokenService"/>);
    /// <c>null</c> uses <see cref="Lifetime"/>.
    /// </summary>
    /// <remarks>
    /// Separate because <see cref="Lifetime"/> also governs service-client tokens: keeping a person's
    /// token short — it bounds how long a deactivation or a narrowed permission set takes to arrive —
    /// should not shorten every service client's.
    /// </remarks>
    public TimeSpan? UserAccessTokenLifetime { get; set; }

    /// <summary>
    /// Lets the host start in a container, outside Development, with no Data Protection key repository
    /// configured — cookie sessions then end with the container. Off by default: such a host is refused at
    /// startup, because a redeploy would sign every web user out.
    /// </summary>
    /// <remarks>
    /// For a deployment that knows and accepts it (a throwaway demo). Everything else persists the key ring —
    /// <c>PersistKeysToDbContext</c> or <c>PersistKeysToFileSystem</c> on a volume.
    /// </remarks>
    public bool AllowContainerLocalKeyRing { get; set; }
}
