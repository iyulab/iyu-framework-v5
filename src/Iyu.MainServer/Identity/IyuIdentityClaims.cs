using System.Security.Claims;

namespace Iyu.MainServer.Identity;

/// <summary>Helpers for the claims the identity authorization policies require.
/// Cookie sign-in in a consuming app MUST emit one permission claim per granted code,
/// using the same claim type the policies check (default "perm").</summary>
public static class IyuIdentityClaims
{
    public const string DefaultPermissionClaimType = "perm";

    public static Claim Permission(string code, string claimType = DefaultPermissionClaimType)
        => new(claimType, code);
}
