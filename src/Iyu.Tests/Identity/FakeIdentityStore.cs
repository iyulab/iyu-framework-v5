using Iyu.Core.Identity;
using Iyu.MainServer.Identity;

namespace Iyu.Tests.Identity;

public sealed class FakeIdentityStore : IIdentityStore, IServiceClientStore
{
    private sealed record FakeUser(Guid Id, string Username, string PasswordHash, string DisplayName,
        string? Email, bool IsActive, DateTimeOffset? LastLoginAt) : IUser;
    /// <remarks>
    /// <c>CreatedAt</c> is declared here rather than inherited: this fake satisfies
    /// <see cref="IServiceClient"/> directly and derives from no entity base, which makes it the
    /// implementer shape that has no timestamp handed to it. That the store must still produce one
    /// for <see cref="ServiceClientSummary"/> is the contract, and this fake is the proof it can be
    /// met without an entity base.
    /// </remarks>
    private sealed record FakeClient(Guid Id, string ClientId, string SecretHash, string DisplayName,
        Guid OwnerUserId, bool IsActive, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt,
        DateTimeOffset CreatedAt) : IServiceClient;

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
        DateTimeOffset? expiresAt = null, IEnumerable<string>? perms = null,
        string displayName = "tool", DateTimeOffset? createdAt = null)
    {
        var id = Guid.NewGuid();
        // Distinct per insertion so ordering assertions are about the store, not about clock resolution.
        _clients.Add(new FakeClient(id, clientId, secretHash, displayName, owner, active, expiresAt, null,
            createdAt ?? _epoch.AddMinutes(_clients.Count)));
        _clientPerms[id] = perms?.ToList() ?? new();
        return id;
    }

    private static readonly DateTimeOffset _epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
        var idx = _clients.FindIndex(c => c.Id == serviceClientId);
        if (idx >= 0) _clients[idx] = _clients[idx] with { LastUsedAt = when };
        return Task.CompletedTask;
    }

    /// <remarks>
    /// Revoked clients are included, marked inactive — a listing that hid them would answer
    /// "is that credential still out there?" the same way as "it never existed".
    /// </remarks>
    public Task<IReadOnlyList<ServiceClientSummary>> ListServiceClientsByOwnerAsync(Guid ownerUserId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ServiceClientSummary>>(_clients
            .Where(c => c.OwnerUserId == ownerUserId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ServiceClientSummary(
                c.Id, c.ClientId, c.DisplayName,
                _clientPerms.TryGetValue(c.Id, out var p) ? p : [],
                c.CreatedAt, c.ExpiresAt, c.LastUsedAt, c.IsActive))
            .ToList());

    public Task<Guid> InsertAsync(string clientId, string secretHash, string displayName, Guid ownerUserId,
        DateTimeOffset? expiresAt, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        // displayName was being dropped here, which no assertion could see until the listing
        // surfaced it. A fake that quietly discards an argument is a fake that agrees with any
        // implementation.
        var id = AddClient(clientId, secretHash, ownerUserId, active: true, expiresAt: expiresAt,
            perms: permissions, displayName: displayName);
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

    public Task<bool> UpdatePermissionsAsync(Guid id, Guid ownerUserId, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        var idx = _clients.FindIndex(c => c.Id == id && c.OwnerUserId == ownerUserId);
        if (idx < 0) return Task.FromResult(false);
        _clientPerms[id] = permissions.ToList();
        return Task.FromResult(true);
    }
}
