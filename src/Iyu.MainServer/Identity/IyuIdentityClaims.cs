using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Iyu.MainServer.Identity;

/// <summary>
/// The claims the identity runtime writes and reads: the permission claim the authorization policies
/// require, and the identifier of whoever is signed in.
/// </summary>
/// <remarks>
/// A person's session is assembled by <see cref="UserSessionService"/> (cookie) or
/// <see cref="UserTokenService"/> (access token) from the application's <see cref="IUserClaimsSource"/> —
/// the permission claims come from that answer, and the identifier is written by the framework.
/// </remarks>
public static class IyuIdentityClaims
{
    public const string DefaultPermissionClaimType = "perm";

    /// <summary>
    /// Carried by every service-client access token: a keyed fingerprint of the client's secret and
    /// effective permissions at issuance. The bearer handler compares it with the client's current
    /// state, which is how revoking, rotating or re-scoping a client reaches tokens already issued.
    /// A token without it (one issued to a person) is not checked this way.
    /// </summary>
    public const string ServiceClientStampClaimType = "sc_stamp";

    public static Claim Permission(string code, string claimType = DefaultPermissionClaimType)
        => new(claimType, code);

    /// <summary>
    /// The identifier of whoever <paramref name="principal"/> is — the subject a person's session was
    /// opened for, or a service client's id — or <c>null</c> when it carries none.
    /// </summary>
    /// <remarks>
    /// One read for both forms of session. A cookie carries the identifier as
    /// <see cref="ClaimTypes.NameIdentifier"/>; an access token carries it as <c>sub</c>, which the bearer
    /// handler presents as <see cref="ClaimTypes.NameIdentifier"/> too unless the host turned inbound
    /// claim mapping off — this reads either.
    /// </remarks>
    public static string? FindSubject(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }

    /// <summary>
    /// The claims of a session for <paramref name="subject"/>: the source's answer with the subject's
    /// identifier written once, under <paramref name="identifierType"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The answer carries an identifier claim naming someone other than the subject asked about.
    /// </exception>
    internal static List<Claim> ForSubject(IEnumerable<Claim> answered, string subject, string identifierType)
    {
        var claims = new List<Claim> { new(identifierType, subject) };
        foreach (var claim in answered)
        {
            if (claim.Type is not (ClaimTypes.NameIdentifier or JwtRegisteredClaimNames.Sub))
            {
                claims.Add(claim);
                continue;
            }

            // The same identifier restated is dropped — the framework has already written it, in the
            // spelling this form of session uses. A different one would make the session someone else's.
            if (!string.Equals(claim.Value, subject, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The claims source was asked about subject '{subject}' and answered an identifier claim "
                    + $"('{claim.Type}') naming '{claim.Value}'. The identifier is written by the framework from "
                    + "the subject; a claims source answers what the subject may do, not who they are.");
        }
        return claims;
    }
}
