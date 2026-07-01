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
}
