namespace Iyu.MainServer.Identity;

/// <summary>
/// One issued refresh token, as stored. The token itself is never stored — only its hash.
/// </summary>
/// <param name="Id">This token's identity.</param>
/// <param name="FamilyId">
/// Every token rotated from the same sign-in shares it. Presenting a token that was already used
/// revokes the whole family: the server cannot tell which of the two holders is the thief.
/// </param>
/// <param name="Subject">Who the token was issued to — the value <see cref="IUserTokenClaimsSource"/> is asked about.</param>
/// <param name="TokenHash">The token's hash (<see cref="UserTokenService.HashRefreshToken"/>), the lookup key.</param>
/// <param name="IssuedAt">When it was issued.</param>
/// <param name="ExpiresAt">When it stops being accepted.</param>
/// <param name="UsedAt">When it was exchanged; a used token is never accepted again.</param>
/// <param name="RevokedAt">When it was revoked, by sign-out, by reuse of a token in its family, or by the claims source refusing.</param>
/// <param name="ParentId">
/// The token this one was issued in exchange for; <c>null</c> for the first token of a sign-in. Lets a
/// retried refresh within <see cref="IdentityTokenOptions.RefreshTokenReuseInterval"/> find, and retire,
/// the replacement its lost response carried.
/// </param>
public sealed record RefreshTokenRecord(
    Guid Id,
    Guid FamilyId,
    string Subject,
    string TokenHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt = null,
    DateTimeOffset? RevokedAt = null,
    Guid? ParentId = null);

/// <summary>
/// Where refresh tokens are kept. The concrete implementation lives in the consuming app, like
/// <see cref="IServiceClientStore"/>. Required, with <see cref="IUserTokenClaimsSource"/>, once the app turns
/// refresh tokens on (<see cref="IyuIdentityServiceCollectionExtensions.AddIyuRefreshTokens"/>).
/// </summary>
/// <remarks>
/// Timestamps are passed in rather than read from the store's own clock — one clock decides, the
/// same rule <see cref="IServiceClientStore.UpdateSecretAsync"/> follows.
/// </remarks>
public interface IRefreshTokenStore
{
    Task InsertAsync(RefreshTokenRecord token, CancellationToken ct);

    /// <summary>The token with this hash, whatever its state; <c>null</c> when none was issued.</summary>
    Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>
    /// Marks the token used — <b>only if it is not already used</b> — and says whether this call did it.
    /// </summary>
    /// <remarks>
    /// Must be a single conditional write (<c>UPDATE … SET UsedAt = @at WHERE Id = @id AND UsedAt IS NULL</c>,
    /// true when one row changed). Two requests racing with the same token must not both succeed: the
    /// loser is treated as a reuse, which is what a stolen copy racing the real client looks like.
    /// </remarks>
    Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken ct);

    /// <summary>
    /// The most recently issued token whose <see cref="RefreshTokenRecord.ParentId"/> is
    /// <paramref name="parentId"/>, whatever its state; <c>null</c> when none was.
    /// </summary>
    /// <remarks>
    /// Asked only when a used token is presented again within
    /// <see cref="IdentityTokenOptions.RefreshTokenReuseInterval"/>. Index <c>ParentId</c>.
    /// </remarks>
    Task<RefreshTokenRecord?> FindLatestChildAsync(Guid parentId, CancellationToken ct);

    /// <summary>Revokes every not-yet-revoked token in the family.</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken ct);

    /// <summary>Revokes every not-yet-revoked token issued to the subject — sign-out everywhere.</summary>
    Task RevokeSubjectAsync(string subject, DateTimeOffset revokedAt, CancellationToken ct);
}

/// <summary>
/// Answers, for a subject, the claims a new access token should carry — or that it should get none.
/// Implemented by the consuming app; asked at sign-in and again at every refresh.
/// </summary>
/// <remarks>
/// Asking again at refresh is the point: a user deactivated, or whose permissions shrank, reaches
/// their tokens at the next refresh rather than never. Answer <c>null</c> to refuse — the refresh
/// fails and the token's family is revoked.
/// </remarks>
public interface IUserTokenClaimsSource
{
    Task<IReadOnlyCollection<System.Security.Claims.Claim>?> GetClaimsAsync(string subject, CancellationToken ct);
}
