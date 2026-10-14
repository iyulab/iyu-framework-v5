using Iyu.Core.Identity;
using Iyu.MainServer.Identity;

namespace Iyu.Tests.Identity;

public sealed class FakeIdentityStore : IServiceClientStore, IUserCredentialStore
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
        DateTimeOffset CreatedAt, DateTimeOffset? SecretRotatedAt = null) : IServiceClient;

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

    /// <remarks>
    /// Inactive accounts are returned too, as the contract asks. This fake used to drop them, which made every
    /// inactive account look like an unknown name — a gap in the contract that no test could see past.
    /// </remarks>
    public Task<IUser?> FindUserByUsernameAsync(string username, CancellationToken ct) =>
        Task.FromResult<IUser?>(_users.FirstOrDefault(u => u.Username == username));

    public Task<IServiceClient?> FindServiceClientByClientIdAsync(string clientId, CancellationToken ct) =>
        Task.FromResult<IServiceClient?>(_clients.FirstOrDefault(c => c.ClientId == clientId));

    /// <summary>
    /// The claims source for the accounts added here: an active account's permissions, or <c>null</c> for an unknown or
    /// inactive one — what a service client's owner is asked through.
    /// </summary>
    public IUserClaimsSource Claims => new AccountClaims(this);

    /// <summary>Replaces an account's permissions, or deactivates it (<paramref name="active"/> false).</summary>
    public void SetUser(Guid userId, IEnumerable<string> perms, bool active = true)
    {
        var i = _users.FindIndex(u => u.Id == userId);
        _users[i] = _users[i] with { IsActive = active };
        _userPerms[userId] = perms.ToList();
    }

    private sealed class AccountClaims(FakeIdentityStore store) : IUserClaimsSource
    {
        public Task<IReadOnlyCollection<System.Security.Claims.Claim>?> GetClaimsAsync(string subject, CancellationToken ct)
        {
            var user = Guid.TryParse(subject, out var id) ? store._users.FirstOrDefault(u => u.Id == id) : null;
            return Task.FromResult<IReadOnlyCollection<System.Security.Claims.Claim>?>(user is { IsActive: true }
                ? [.. store._userPerms[id].Select(p => IyuIdentityClaims.Permission(p))]
                : null);
        }
    }

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
        Task.FromResult(Summaries(_clients.Where(c => c.OwnerUserId == ownerUserId)));

    public Task<IReadOnlyList<ServiceClientSummary>> ListServiceClientsAsync(CancellationToken ct) =>
        Task.FromResult(Summaries(_clients));

    private IReadOnlyList<ServiceClientSummary> Summaries(IEnumerable<FakeClient> clients) => clients
        .OrderByDescending(c => c.CreatedAt)
        .Select(c => new ServiceClientSummary(
            c.Id, c.OwnerUserId, c.ClientId, c.DisplayName,
            _clientPerms.TryGetValue(c.Id, out var p) ? p : [],
            c.CreatedAt, c.ExpiresAt, c.LastUsedAt, c.SecretRotatedAt, c.IsActive))
        .ToList();

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

    public Task<bool> DeactivateAsync(Guid id, CancellationToken ct)
    {
        var idx = _clients.FindIndex(c => c.Id == id);
        if (idx < 0) return Task.FromResult(false);
        _clients[idx] = _clients[idx] with { IsActive = false };
        return Task.FromResult(true);
    }

    public Task<int> DeactivateByOwnerAsync(Guid ownerUserId, CancellationToken ct)
    {
        var revoked = 0;
        for (var i = 0; i < _clients.Count; i++)
        {
            if (_clients[i].OwnerUserId != ownerUserId || !_clients[i].IsActive) continue;
            _clients[i] = _clients[i] with { IsActive = false };
            revoked++;
        }
        return Task.FromResult(revoked);
    }

    public Task<bool> UpdateSecretAsync(Guid id, Guid ownerUserId, string newSecretHash,
        DateTimeOffset rotatedAt, CancellationToken ct)
    {
        var idx = _clients.FindIndex(c => c.Id == id && c.OwnerUserId == ownerUserId);
        if (idx < 0) return Task.FromResult(false);
        // Storing the timestamp is the half a fake most easily drops: nothing else reads it, so a
        // fake that ignored it would agree with an implementation that never persisted a rotation.
        _clients[idx] = _clients[idx] with { SecretHash = newSecretHash, SecretRotatedAt = rotatedAt };
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
