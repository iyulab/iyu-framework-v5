namespace Iyu.MainServer.Identity;

/// <summary>
/// How logout notices from OpenID Connect providers are accepted
/// (<see cref="IyuBackChannelLogoutExtensions.AddIyuBackChannelLogout"/>).
/// </summary>
public sealed class BackChannelLogoutOptions
{
    /// <summary>
    /// Whether a logout token must carry an expiry (<c>exp</c>). Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// OpenID Connect Back-Channel Logout 1.0 requires the claim, and providers in use send logout tokens without
    /// it. A token that carries one is always held to it; this decides only what happens to one that does not —
    /// refused when <c>true</c>, accepted while it is young enough (<see cref="MaxAgeWithoutExpiration"/>) otherwise.
    /// Turn it on when your provider is known to send the claim.
    /// </remarks>
    public bool RequireExpiration { get; set; }

    /// <summary>
    /// How old a logout token without an expiry may be, counted from when it was issued (<c>iat</c>, which is always
    /// required). Default five minutes. The handler's clock skew is allowed on top.
    /// </summary>
    /// <remarks>
    /// A notice is sent the moment a session ends, so a token that arrives much later is a replay or a clock that is
    /// wrong. The window only bounds how long a captured token stays usable; replaying one inside it ends a session
    /// that has already ended.
    /// </remarks>
    public TimeSpan MaxAgeWithoutExpiration { get; set; } = TimeSpan.FromMinutes(5);
}
