using System.Security.Claims;

namespace Iyu.MainServer.Identity;

/// <summary>Helpers for the claims the identity authorization policies require.
/// Cookie sign-in in a consuming app MUST emit one permission claim per granted code,
/// using the same claim type the policies check (default "perm").</summary>
public static class IyuIdentityClaims
{
    public const string DefaultPermissionClaimType = "perm";

    /// <summary>
    /// Carried by every service-client access token: a keyed fingerprint of the client's secret and
    /// effective permissions at issuance. The bearer handler compares it with the client's current
    /// state, which is how revoking, rotating or re-scoping a client reaches tokens already issued.
    /// A token without it (one a consuming app signed for a person) is not checked this way.
    /// </summary>
    public const string ServiceClientStampClaimType = "sc_stamp";

    public static Claim Permission(string code, string claimType = DefaultPermissionClaimType)
        => new(claimType, code);
}
