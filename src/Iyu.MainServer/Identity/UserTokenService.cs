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
/// because the server cannot tell the legitimate client from whoever copied it (unless it is a retry
/// within <see cref="IdentityTokenOptions.RefreshTokenReuseInterval"/>, off by default); and each refresh
/// asks <see cref="IUserClaimsSource"/> again, so deactivation or a narrowed permission set
/// reaches the person's tokens at the next refresh.
/// </para>
/// <para>
/// Refresh tokens are 256 random bits; only their SHA-256 hash is stored. A random value that long
/// needs no salt or slow hash — there is nothing to guess.
/// </para>
/// <para>
/// Registered by <see cref="IyuIdentityServiceCollectionExtensions.AddIyuRefreshTokens"/>, alongside the app's
/// <see cref="IRefreshTokenStore"/>; <c>POST /api/auth/token</c> then
/// also accepts <c>grant_type=refresh_token</c>.
/// </para>
/// </remarks>
public sealed class UserTokenService
{
    private readonly UserAccessTokenService _access;
    private readonly IRefreshTokenStore _store;
    private readonly IdentityTokenOptions _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserTokenService> _log;

    public UserTokenService(UserAccessTokenService access, IRefreshTokenStore store, IdentityTokenOptions opts,
        TimeProvider clock, ILogger<UserTokenService> log)
    {
        _access = access; _store = store; _opts = opts; _clock = clock; _log = log;
    }

    /// <summary>
    /// Starts a sign-in: an access token with the claims <see cref="IUserClaimsSource"/> gives for
    /// <paramref name="subject"/>, and the first refresh token of a new family.
    /// </summary>
    /// <remarks>
    /// Call only after the app has authenticated the person — this method checks no credential. The
    /// claims come from the same source a refresh asks, so a first token and a refreshed one cannot
    /// disagree about who the person is; the token's <c>sub</c> is <paramref name="subject"/>.
    /// <c>invalid_grant</c> when the source refuses the subject.
    /// </remarks>
    public Task<UserTokenPair> IssueAsync(string subject, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return IssuePairAsync(subject, Guid.NewGuid(), exchanged: null, ct);
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
        if (token.UsedAt is { } usedAt) return await PresentedAgainAsync(token, usedAt, now, ct);
        if (token.ExpiresAt <= now) return Reject(token, "refresh token has expired");
        if (!await _store.TryMarkUsedAsync(token.Id, now, ct)) return await ReuseAsync(token, now, ct);

        return await IssuePairAsync(token.Subject, token.FamilyId, token, ct);
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

    // exchanged: the refresh token this pair is issued in exchange for; null at sign-in.
    private async Task<UserTokenPair> IssuePairAsync(
        string subject, Guid familyId, RefreshTokenRecord? exchanged, CancellationToken ct)
    {
        // The access half is the same token UserAccessTokenService issues on its own — assembled from the source.
        var (access, stamp) = await _access.IssueWithStampAsync(subject, lifetime: null, ct);
        var now = _clock.GetUtcNow();
        if (!access.Ok)
        {
            // The source refused the subject (logged there): the sign-in ends with it.
            await _store.RevokeFamilyAsync(familyId, now, ct);
            return UserTokenPair.Refused;
        }

        // A refresh extends the sign-in it descends from, and that sign-in belongs to the state of the account
        // it was opened in. The source has just answered the account as it is now; a different stamp means the
        // sign-in was ended since (a password change, a sign-out everywhere), so the family ends here.
        if (exchanged is not null && !IyuIdentityClaims.SameSecurityStamp(exchanged.SecurityStamp, stamp))
        {
            await _store.RevokeFamilyAsync(familyId, now, ct);
            return Reject(exchanged, exchanged.SecurityStamp is null
                ? "the account now has a security stamp and the refresh token carries none — it was issued before "
                  + "the claims source answered one, or the store does not persist RefreshTokenRecord.SecurityStamp; "
                  + "its family is revoked"
                : "the account's security stamp is not the one the refresh token was issued under — its family is revoked");
        }

        var refresh = Base64Url(RandomNumberGenerator.GetBytes(32));
        await _store.InsertAsync(new RefreshTokenRecord(Guid.NewGuid(), familyId, subject, HashRefreshToken(refresh),
            now, now.Add(_opts.RefreshTokenLifetime), ParentId: exchanged?.Id, SecurityStamp: stamp), ct);
        return new(true, null, access.AccessToken, access.ExpiresInSeconds, refresh, access.Permissions);
    }

    /// <summary>
    /// A used token, presented again. A retry within <see cref="IdentityTokenOptions.RefreshTokenReuseInterval"/>,
    /// the replacement it was exchanged for not yet used, gets a new pair, and that replacement is retired
    /// so the sign-in keeps one live refresh token. Anything else is reuse.
    /// </summary>
    /// <remarks>
    /// The replacement cannot be handed back: only its hash is stored. It is retired with the same
    /// conditional used-mark an exchange takes, so two retries racing for it cannot both succeed, and a
    /// replacement that was already used (the client did receive it and moved on) means this
    /// presentation is a copy.
    /// </remarks>
    private async Task<UserTokenPair> PresentedAgainAsync(
        RefreshTokenRecord token, DateTimeOffset usedAt, DateTimeOffset now, CancellationToken ct)
    {
        var interval = _opts.RefreshTokenReuseInterval;
        if (interval <= TimeSpan.Zero || now - usedAt > interval) return await ReuseAsync(token, now, ct);

        // The conditional used-mark is the whole test: it fails for a replacement already used (the
        // client received it and moved on) and for the loser of two retries racing for it. A revoked
        // replacement cannot get here — revocation takes the family, and the presented token with it.
        var replacement = await _store.FindLatestChildAsync(token.Id, ct);
        if (replacement is null || !await _store.TryMarkUsedAsync(replacement.Id, now, ct))
            return await ReuseAsync(token, now, ct);

        _log.LogInformation(
            "refresh token presented again within the reuse interval for {Subject} (family {FamilyId}); its unused replacement is retired",
            token.Subject, token.FamilyId);
        return await IssuePairAsync(token.Subject, token.FamilyId, token, ct);
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
