namespace Iyu.MainServer.Identity;

/// <summary>Write side for service-client lifecycle. Concrete impl lives in the consuming app (EF).</summary>
public interface IServiceClientStore
{
    Task<Guid> InsertAsync(string clientId, string secretHash, string displayName, Guid ownerUserId,
        DateTimeOffset? expiresAt, IReadOnlyList<string> permissions, CancellationToken ct);
    Task<bool> DeactivateAsync(Guid id, Guid ownerUserId, CancellationToken ct);
    Task<bool> UpdateSecretAsync(Guid id, Guid ownerUserId, string newSecretHash, CancellationToken ct);
}
