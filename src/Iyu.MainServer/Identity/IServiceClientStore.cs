using Iyu.Core.Identity;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Everything the service-client surface reads and writes — the one port
/// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuServiceClients"/> needs. Concrete impl lives in the
/// consuming app (EF).
/// </summary>
/// <remarks>
/// What a client's owner holds is not asked here: it is what <see cref="IUserClaimsSource"/> answers for the owner —
/// the same answer the owner's own session is assembled from.
/// </remarks>
public interface IServiceClientStore
{
    Task<IServiceClient?> FindServiceClientByClientIdAsync(string clientId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetServiceClientPermissionsAsync(Guid serviceClientId, CancellationToken ct);
    Task TouchServiceClientAsync(Guid serviceClientId, DateTimeOffset when, CancellationToken ct);

    /// <summary>
    /// Every service client owned by <paramref name="ownerUserId"/>, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Return revoked clients too, marked <see cref="ServiceClientSummary.IsActive"/> false.</b>
    /// The reason this surface exists is that a credential must stay reachable after something has
    /// gone wrong with it; a listing that drops revoked entries answers "is it still out there?"
    /// with silence, which reads the same as "it never existed".
    /// </para>
    /// <para>
    /// <b>Scope strictly to the owner.</b> Another user's clients are not omitted as a courtesy —
    /// they are invisible, the same way <c>revoke</c> and <c>rotate</c> answer 404 rather than 403
    /// for a client the caller does not own.
    /// </para>
    /// <para>
    /// <b>Resolve permissions in the same query.</b> Calling
    /// <see cref="GetServiceClientPermissionsAsync"/> per row turns one listing into N+1 round
    /// trips; the summary carries them so the store can join once.
    /// </para>
    /// <para>
    /// <b>No implementation is provided deliberately.</b> A default returning an empty list would
    /// let an un-updated store compile and then tell every owner they have issued nothing — the
    /// exact failure this endpoint exists to fix, in a quieter form.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ServiceClientSummary>> ListServiceClientsByOwnerAsync(Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Every service client of every owner, newest first — what an administrator reads to find the credentials an
    /// account issued (<see cref="IdentityEndpointRouteExtensions.MapIyuServiceClientAdministration"/>).
    /// </summary>
    /// <remarks>
    /// The same contract as <see cref="ListServiceClientsByOwnerAsync"/> otherwise: revoked clients included and marked,
    /// permissions resolved in the same query, no default implementation.
    /// </remarks>
    Task<IReadOnlyList<ServiceClientSummary>> ListServiceClientsAsync(CancellationToken ct);

    Task<Guid> InsertAsync(string clientId, string secretHash, string displayName, Guid ownerUserId,
        DateTimeOffset? expiresAt, IReadOnlyList<string> permissions, CancellationToken ct);

    /// <summary>Revokes a client of <paramref name="ownerUserId"/> — <c>false</c> when it has no such client.</summary>
    /// <remarks>One conditional write (<c>… WHERE Id = @id AND OwnerUserId = @owner</c>), not a lookup and a write.</remarks>
    Task<bool> DeactivateAsync(Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>Revokes a client whoever owns it — <c>false</c> when there is none. An administrator's revocation.</summary>
    Task<bool> DeactivateAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Revokes every active client of <paramref name="ownerUserId"/> and says how many it revoked — the credentials a
    /// compromised account issued, retired at once.
    /// </summary>
    Task<int> DeactivateByOwnerAsync(Guid ownerUserId, CancellationToken ct);
    /// <summary>
    /// Replaces the stored secret hash and records when that happened.
    /// </summary>
    /// <remarks>
    /// <paramref name="rotatedAt"/> is passed in rather than read from the store's own clock, the
    /// same way <see cref="TouchServiceClientAsync"/> takes its timestamp: one clock
    /// decides, so a listing can compare a rotation against a last-use without the two having been
    /// stamped by different machines. Persist it — <c>ServiceClientSummary.SecretRotatedAt</c> is
    /// where it surfaces, and an owner cannot tell a rotated credential from a dead one without it.
    /// </remarks>
    Task<bool> UpdateSecretAsync(Guid id, Guid ownerUserId, string newSecretHash,
        DateTimeOffset rotatedAt, CancellationToken ct);
    Task<bool> UpdatePermissionsAsync(Guid id, Guid ownerUserId, IReadOnlyList<string> permissions, CancellationToken ct);
}
