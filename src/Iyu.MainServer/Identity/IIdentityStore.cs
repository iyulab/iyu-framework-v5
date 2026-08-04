using Iyu.Core.Identity;

namespace Iyu.MainServer.Identity;

/// <summary>Read/side-effect surface the identity runtime needs, decoupled from concrete entity types.</summary>
public interface IIdentityStore
{
    Task<IUser?> FindUserByUsernameAsync(string username, CancellationToken ct);
    Task<IServiceClient?> FindServiceClientByClientIdAsync(string clientId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct);
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
}
