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
    /// The lifetime of a person's access token issued with a refresh token (<see cref="UserTokenService"/>);
    /// <c>null</c> uses <see cref="Lifetime"/>.
    /// </summary>
    /// <remarks>
    /// Separate because <see cref="Lifetime"/> also governs service-client tokens: keeping a person's
    /// token short — it bounds how long a deactivation or a narrowed permission set takes to arrive —
    /// should not shorten every service client's.
    /// </remarks>
    public TimeSpan? UserAccessTokenLifetime { get; set; }
}
