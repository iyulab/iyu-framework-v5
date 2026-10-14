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

    /// <summary>
    /// The claim a claims source answers to say which state of the account a session belongs to — its
    /// <em>security stamp</em>. A session, or a refresh token, opened under one stamp ends when the source
    /// answers another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answer a value that changes whenever every open session of the account must end: the password was
    /// changed or reset, a second factor was added or removed, the person asked to be signed out everywhere.
    /// The framework keeps the value the session was opened with and compares it each time it asks the source
    /// again — the cookie at its revalidation (<see cref="IdentityTokenOptions.SessionRevalidationInterval"/>),
    /// a refresh token when it is presented. A different value ends the session rather than refreshing it.
    /// </para>
    /// <para>
    /// A source that never answers such a claim keeps what it had before: sessions follow the account's claims
    /// and end only when the source refuses the subject. The comparison runs both ways. Once a source starts
    /// answering one, a session or refresh token opened without it has no stamp to compare and is ended the next
    /// time the source is asked; and one opened under a stamp is ended the same way if the source stops
    /// answering it — an account that has a stamp keeps having one.
    /// </para>
    /// <para>
    /// The value travels in the session cookie and in access tokens, and an access token is readable by whoever
    /// holds it. Answer something that reveals nothing — a random value stored with the account and replaced when
    /// sessions must end, or a keyed digest — never a credential hash itself.
    /// </para>
    /// </remarks>
    public const string SecurityStampClaimType = "sec_stamp";

    /// <summary>
    /// Carried by a person's access token issued with a refresh token: the session — the sign-in — it belongs to. Where
    /// session ends are on, a token whose session has ended is refused (<see cref="SessionEndService"/>). Written by the
    /// framework; a claim of this name in a claims source's answer is not carried into such a token.
    /// </summary>
    internal const string SessionIdClaimType = "sid";

    public static Claim Permission(string code, string claimType = DefaultPermissionClaimType)
        => new(claimType, code);

    /// <summary>The claim that states an account's security stamp — see <see cref="SecurityStampClaimType"/>.</summary>
    public static Claim SecurityStamp(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new(SecurityStampClaimType, value);
    }

    /// <summary>
    /// The security stamp among <paramref name="claims"/>, or <c>null</c> when they state none. Two different
    /// values are a source's mistake — a session cannot belong to two states of the account.
    /// </summary>
    internal static string? FindSecurityStamp(IEnumerable<Claim> claims)
    {
        string? stamp = null;
        foreach (var claim in claims)
        {
            if (claim.Type != SecurityStampClaimType) continue;
            if (stamp is not null && !string.Equals(stamp, claim.Value, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The claims carry more than one '{SecurityStampClaimType}' value. A claims source answers "
                    + "one security stamp for an account, or none.");
            stamp = claim.Value;
        }
        return stamp;
    }

    /// <summary>Whether a session opened under <paramref name="opened"/> still belongs to the account as it is now.</summary>
    internal static bool SameSecurityStamp(string? opened, string? now) => string.Equals(opened, now, StringComparison.Ordinal);

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
