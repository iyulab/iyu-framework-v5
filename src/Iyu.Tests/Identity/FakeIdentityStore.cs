using Iyu.Core.Identity;
using Iyu.MainServer.Identity;

namespace Iyu.Tests.Identity;

public sealed class FakeIdentityStore : IIdentityStore, IServiceClientStore
{
    private sealed record FakeUser(Guid Id, string Username, string PasswordHash, string DisplayName,
        string? Email, bool IsActive, DateTimeOffset? LastLoginAt) : IUser;
    private sealed record FakeClient(Guid Id, string ClientId, string SecretHash, string DisplayName,
        Guid OwnerUserId, bool IsActive, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt) : IServiceClient;

    private readonly List<FakeUser> _users = new();
    private readonly List<FakeClient> _clients = new();
    private readonly Dictionary<Guid, List<string>> _userPerms = new();
    private readonly Dictionary<Guid, List<string>> _clientPerms = new();
    public readonly List<Guid> Touched = new();

    public Guid AddUser(string username, string display, string passwordHash = "h", bool active = true, IEnumerable<string>? perms = null)
    {
        var id = Guid.NewGuid();
        _users.Add(new FakeUser(id, username, passwordHash, display, null, active, null));
        _userPerms[id] = perms?.ToList() ?? new();
        return id;
    }

    public Guid AddClient(string clientId, string secretHash, Guid owner, bool active = true,
        DateTimeOffset? expiresAt = null, IEnumerable<string>? perms = null)
    {
        var id = Guid.NewGuid();
        _clients.Add(new FakeClient(id, clientId, secretHash, "tool", owner, active, expiresAt, null));
        _clientPerms[id] = perms?.ToList() ?? new();
        return id;
    }

    public Task<IUser?> FindUserByUsernameAsync(string username, CancellationToken ct) =>
        Task.FromResult<IUser?>(_users.FirstOrDefault(u => u.Username == username && u.IsActive));

    public Task<IServiceClient?> FindServiceClientByClientIdAsync(string clientId, CancellationToken ct) =>
        Task.FromResult<IServiceClient?>(_clients.FirstOrDefault(c => c.ClientId == clientId));

    public Task<IReadOnlyList<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(_userPerms.TryGetValue(userId, out var p) ? p : new());

    public Task<IReadOnlyList<string>> GetServiceClientPermissionsAsync(Guid serviceClientId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(_clientPerms.TryGetValue(serviceClientId, out var p) ? p : new());

    public Task TouchServiceClientAsync(Guid serviceClientId, DateTimeOffset when, CancellationToken ct)
    {
        Touched.Add(serviceClientId);
        return Task.CompletedTask;
    }

    public Task<Guid> InsertAsync(string clientId, string secretHash, string displayName, Guid ownerUserId,
        DateTimeOffset? expiresAt, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        var id = AddClient(clientId, secretHash, ownerUserId, active: true, expiresAt: expiresAt, perms: permissions);
        return Task.FromResult(id);
    }

    public Task<bool> DeactivateAsync(Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var idx = _clients.FindIndex(c => c.Id == id && c.OwnerUserId == ownerUserId);
        if (idx < 0) return Task.FromResult(false);
        _clients[idx] = _clients[idx] with { IsActive = false };
        return Task.FromResult(true);
    }

    public Task<bool> UpdateSecretAsync(Guid id, Guid ownerUserId, string newSecretHash, CancellationToken ct)
    {
        var idx = _clients.FindIndex(c => c.Id == id && c.OwnerUserId == ownerUserId);
        if (idx < 0) return Task.FromResult(false);
        _clients[idx] = _clients[idx] with { SecretHash = newSecretHash };
        return Task.FromResult(true);
    }
}
