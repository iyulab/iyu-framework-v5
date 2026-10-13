using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Issues a person's access token — the bearer counterpart of <see cref="UserSessionService.SignInAsync"/>, for a
/// client that cannot hold a cookie and does not use refresh tokens.
/// </summary>
/// <remarks>
/// <para>
/// The application still decides who the person is (a password it checked, an external provider's identity it
/// matched to an account) and then calls <see cref="IssueAsync"/> with that account's subject. The token is
/// assembled here, the same way a session cookie and a refreshed token are: the claims
/// <see cref="IUserClaimsSource"/> answers for the subject, and <c>sub</c> written from the subject. Nothing here
/// checks a credential.
/// </para>
/// <para>
/// <b>An access token cannot be taken back.</b> Nothing asks the source again while it lives, so a person
/// deactivated after it was issued keeps using it until it expires. Keep it short, or use
/// <see cref="UserTokenService"/> — a refresh asks the source again — for a client that stays signed in.
/// </para>
/// <para>
/// Registered by <see cref="IyuIdentityServiceCollectionExtensions.AddIyuIdentity"/>; it needs nothing beyond the
/// claims source every host already registers.
/// </para>
/// <para>
/// There is no public way to sign a person's token over claims the caller put together: nothing re-reads a bearer
/// token while it lives, so such a token would carry whatever permissions the caller wrote, for as long as it lasts.
/// </para>
/// </remarks>
public sealed class UserAccessTokenService
{
    private readonly IUserClaimsSource _claims;
    private readonly IdentityTokenOptions _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserAccessTokenService> _log;

    public UserAccessTokenService(IUserClaimsSource claims, IdentityTokenOptions opts, TimeProvider clock,
        ILogger<UserAccessTokenService> log)
    {
        _claims = claims; _opts = opts; _clock = clock; _log = log;
    }

    /// <summary>
    /// An access token for <paramref name="subject"/>, carrying the claims the source answers.
    /// </summary>
    /// <param name="subject">The account the application authenticated; the token's <c>sub</c>.</param>
    /// <param name="lifetime">
    /// How long the token is valid. Unset, <see cref="IdentityTokenOptions.UserAccessTokenLifetime"/>, or
    /// <see cref="IdentityTokenOptions.Lifetime"/> when that is unset too. A longer one serves a client that works
    /// offline and cannot come back for another — at the price stated on this type.
    /// </param>
    /// <param name="ct">Cancels the question to the claims source.</param>
    /// <returns>
    /// The token, or — when the source refuses the subject — <c>Ok = false</c> with <c>invalid_grant</c> and no token.
    /// </returns>
    public async Task<TokenResult> IssueAsync(string subject, TimeSpan? lifetime = null, CancellationToken ct = default)
        => (await IssueWithStampAsync(subject, lifetime, ct)).Token;

    /// <summary>
    /// <see cref="IssueAsync"/>, and the security stamp the source answered with the claims — what a refresh
    /// token issued alongside is opened under.
    /// </summary>
    internal async Task<(TokenResult Token, string? SecurityStamp)> IssueWithStampAsync(
        string subject, TimeSpan? lifetime, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (lifetime is { } requested && requested <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), requested, "Lifetime must be positive.");

        var claims = await _claims.GetClaimsAsync(subject, ct);
        if (claims is null)
        {
            _log.LogWarning("access token refused for {Subject}: the claims source declined the subject", subject);
            return (new(false, "invalid_grant", null, 0, []), null);
        }

        // The subject is written here, not left to the source: a token and a cookie opened for the same person
        // then name them the same way, whatever the source answered.
        var assembled = IyuIdentityClaims.ForSubject(claims, subject, JwtRegisteredClaimNames.Sub).ToList();
        var effective = lifetime ?? _opts.UserAccessTokenLifetime ?? _opts.Lifetime;
        if (effective <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{nameof(IdentityTokenOptions)}.{nameof(IdentityTokenOptions.UserAccessTokenLifetime)} (or "
                + $"{nameof(IdentityTokenOptions.Lifetime)}) must be positive.");

        var jwt = IdentityTokenSigning.Sign(_opts, assembled, effective, _clock.GetUtcNow());
        var permissions = assembled.Where(c => c.Type == _opts.PermissionClaimType).Select(c => c.Value).ToArray();
        return (new(true, null, jwt, (int)effective.TotalSeconds, permissions),
            IyuIdentityClaims.FindSecurityStamp(assembled));
    }
}
