using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// An access token and the refresh token that replaces it — or why none was issued.
/// </summary>
/// <param name="Ok">Whether tokens were issued.</param>
/// <param name="Error">The RFC 6749 §5.2 error code when not: <c>invalid_grant</c>.</param>
/// <param name="AccessToken">The signed access token.</param>
/// <param name="ExpiresInSeconds">The access token's lifetime.</param>
/// <param name="RefreshToken">An opaque value to exchange, once, for the next pair.</param>
/// <param name="Permissions">The permission claims the access token carries.</param>
public sealed record UserTokenPair(bool Ok, string? Error, string? AccessToken, int ExpiresInSeconds,
    string? RefreshToken, IReadOnlyList<string> Permissions)
{
    internal static UserTokenPair Refused { get; } = new(false, "invalid_grant", null, 0, null, []);
}

/// <summary>
/// Short-lived access tokens with rotating refresh tokens for people — the flow a public client
/// (a native or desktop app) needs instead of one long-lived access token (RFC 9700 §4.14,
/// RFC 8252).
/// </summary>
/// <remarks>
/// <para>
/// The consuming app still authenticates the person (password, external identity provider) and
/// then calls <see cref="IssueAsync"/>. From there: each refresh token is accepted once and replaced
/// (rotation); a token presented a second time revokes every token descended from the same sign-in,
/// because the server cannot tell the legitimate client from whoever copied it; and each refresh
/// asks <see cref="IUserTokenClaimsSource"/> again, so deactivation or a narrowed permission set
/// reaches the person's tokens at the next refresh.
/// </para>
/// <para>
/// Refresh tokens are 256 random bits; only their SHA-256 hash is stored. A random value that long
/// needs no salt or slow hash — there is nothing to guess.
/// </para>
/// <para>
/// Available when the app registers <see cref="IRefreshTokenStore"/> and <see cref="IUserTokenClaimsSource"/>;
/// <c>POST /api/auth/token</c> then also accepts <c>grant_type=refresh_token</c>.
/// </para>
/// </remarks>
public sealed class UserTokenService
{
    private readonly IdentityTokenService _tokens;
    private readonly IRefreshTokenStore _store;
    private readonly IUserTokenClaimsSource _claims;
    private readonly IdentityTokenOptions _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserTokenService> _log;

    public UserTokenService(IdentityTokenService tokens, IRefreshTokenStore store, IUserTokenClaimsSource claims,
        IdentityTokenOptions opts, TimeProvider clock, ILogger<UserTokenService> log)
    {
        _tokens = tokens; _store = store; _claims = claims; _opts = opts; _clock = clock; _log = log;
    }

    /// <summary>
    /// Starts a sign-in: an access token with the claims <see cref="IUserTokenClaimsSource"/> gives for
    /// <paramref name="subject"/>, and the first refresh token of a new family.
    /// </summary>
    /// <remarks>
    /// Call only after the app has authenticated the person — this method checks no credential. The
    /// claims come from the same source a refresh asks, so a first token and a refreshed one cannot
    /// disagree about who the person is. <c>invalid_grant</c> when the source refuses the subject.
    /// </remarks>
    public Task<UserTokenPair> IssueAsync(string subject, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return IssuePairAsync(subject, Guid.NewGuid(), ct);
    }

    /// <summary>
    /// Exchanges a refresh token for the next pair (RFC 6749 §6), retiring the one presented.
    /// </summary>
    /// <remarks>
    /// <c>invalid_grant</c> for a token that is unknown, expired, revoked or already used — one code for
    /// all of them, as §5.2 prescribes. A token already used, or lost in a race with another request
    /// presenting it, revokes its whole family; so does the claims source refusing the subject.
    /// </remarks>
    public async Task<UserTokenPair> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return UserTokenPair.Refused;

        var token = await _store.FindByHashAsync(HashRefreshToken(refreshToken), ct);
        var now = _clock.GetUtcNow();
        if (token is null) return Reject(null, "no such refresh token");
        if (token.RevokedAt is not null) return Reject(token, "refresh token was revoked");
        if (token.UsedAt is not null) return await ReuseAsync(token, now, ct);
        if (token.ExpiresAt <= now) return Reject(token, "refresh token has expired");
        if (!await _store.TryMarkUsedAsync(token.Id, now, ct)) return await ReuseAsync(token, now, ct);

        return await IssuePairAsync(token.Subject, token.FamilyId, ct);
    }

    /// <summary>Ends the sign-in a refresh token belongs to (sign-out on one device). Unknown tokens are ignored.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        if (await _store.FindByHashAsync(HashRefreshToken(refreshToken), ct) is { } token)
            await _store.RevokeFamilyAsync(token.FamilyId, _clock.GetUtcNow(), ct);
    }

    /// <summary>
    /// Ends every sign-in of the subject — sign-out everywhere, a password change, a deactivation.
    /// </summary>
    /// <remarks>
    /// Access tokens already issued stay valid until they expire; that is what keeping them short
    /// (<see cref="IdentityTokenOptions.Lifetime"/>) bounds.
    /// </remarks>
    public Task RevokeAllAsync(string subject, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return _store.RevokeSubjectAsync(subject, _clock.GetUtcNow(), ct);
    }

    /// <summary>The stored form of a refresh token: its SHA-256 hash, base64url.</summary>
    public static string HashRefreshToken(string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }

    private async Task<UserTokenPair> IssuePairAsync(string subject, Guid familyId, CancellationToken ct)
    {
        var claims = await _claims.GetClaimsAsync(subject, ct);
        var now = _clock.GetUtcNow();
        if (claims is null)
        {
            await _store.RevokeFamilyAsync(familyId, now, ct);
            _log.LogWarning("user token refused for {Subject}: the claims source declined the subject", subject);
            return UserTokenPair.Refused;
        }

        var access = _tokens.IssueUserToken(claims, _opts.UserAccessTokenLifetime);
        var refresh = Base64Url(RandomNumberGenerator.GetBytes(32));
        await _store.InsertAsync(new RefreshTokenRecord(Guid.NewGuid(), familyId, subject, HashRefreshToken(refresh),
            now, now.Add(_opts.RefreshTokenLifetime)), ct);
        return new(true, null, access.AccessToken, access.ExpiresInSeconds, refresh, access.Permissions);
    }

    private async Task<UserTokenPair> ReuseAsync(RefreshTokenRecord token, DateTimeOffset now, CancellationToken ct)
    {
        await _store.RevokeFamilyAsync(token.FamilyId, now, ct);
        return Reject(token, "refresh token was presented after it was used — its family is revoked");
    }

    /// <summary>
    /// Logs the cause for the operator; the caller gets <c>invalid_grant</c> whatever it was. Neither
    /// the token nor its hash is logged.
    /// </summary>
    private UserTokenPair Reject(RefreshTokenRecord? token, string reason)
    {
        if (token is null) _log.LogInformation("refresh rejected: {Reason}", reason);
        else _log.LogWarning("refresh rejected for {Subject} (family {FamilyId}): {Reason}", token.Subject, token.FamilyId, reason);
        return UserTokenPair.Refused;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
