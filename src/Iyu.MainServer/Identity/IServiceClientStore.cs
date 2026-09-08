namespace Iyu.MainServer.Identity;

/// <summary>Write side for service-client lifecycle. Concrete impl lives in the consuming app (EF).</summary>
public interface IServiceClientStore
{
    Task<Guid> InsertAsync(string clientId, string secretHash, string displayName, Guid ownerUserId,
        DateTimeOffset? expiresAt, IReadOnlyList<string> permissions, CancellationToken ct);
    Task<bool> DeactivateAsync(Guid id, Guid ownerUserId, CancellationToken ct);
    /// <summary>
    /// Replaces the stored secret hash and records when that happened.
    /// </summary>
    /// <remarks>
    /// <paramref name="rotatedAt"/> is passed in rather than read from the store's own clock, the
    /// same way <c>IIdentityStore.TouchServiceClientAsync</c> takes its timestamp: one clock
    /// decides, so a listing can compare a rotation against a last-use without the two having been
    /// stamped by different machines. Persist it — <c>ServiceClientSummary.SecretRotatedAt</c> is
    /// where it surfaces, and an owner cannot tell a rotated credential from a dead one without it.
    /// </remarks>
    Task<bool> UpdateSecretAsync(Guid id, Guid ownerUserId, string newSecretHash,
        DateTimeOffset rotatedAt, CancellationToken ct);
    Task<bool> UpdatePermissionsAsync(Guid id, Guid ownerUserId, IReadOnlyList<string> permissions, CancellationToken ct);
}
