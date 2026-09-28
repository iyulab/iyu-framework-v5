using System.Security.Claims;
using Iyu.MainServer.Identity;

namespace Iyu.Tests.Identity;

/// <summary>
/// An in-memory <see cref="IRefreshTokenStore"/> that keeps the contract a real one must: the
/// conditional used-mark is atomic.
/// </summary>
public sealed class FakeRefreshTokenStore : IRefreshTokenStore
{
    private readonly object _gate = new();
    private readonly List<RefreshTokenRecord> _tokens = new();

    /// <summary>Makes the next conditional used-mark lose, as if another request had just won it.</summary>
    public bool LoseNextMarkRace { get; set; }

    public IReadOnlyList<RefreshTokenRecord> Tokens { get { lock (_gate) return _tokens.ToList(); } }

    public Task InsertAsync(RefreshTokenRecord token, CancellationToken ct)
    {
        lock (_gate) _tokens.Add(token);
        return Task.CompletedTask;
    }

    public Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_tokens.SingleOrDefault(t => t.TokenHash == tokenHash));
    }

    public Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (LoseNextMarkRace) { LoseNextMarkRace = false; return Task.FromResult(false); }
            var i = _tokens.FindIndex(t => t.Id == id);
            if (i < 0 || _tokens[i].UsedAt is not null) return Task.FromResult(false);
            _tokens[i] = _tokens[i] with { UsedAt = usedAt };
            return Task.FromResult(true);
        }
    }

    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        Revoke(t => t.FamilyId == familyId, revokedAt);
        return Task.CompletedTask;
    }

    public Task RevokeSubjectAsync(string subject, DateTimeOffset revokedAt, CancellationToken ct)
    {
        Revoke(t => t.Subject == subject, revokedAt);
        return Task.CompletedTask;
    }

    private void Revoke(Func<RefreshTokenRecord, bool> match, DateTimeOffset at)
    {
        lock (_gate)
            for (var i = 0; i < _tokens.Count; i++)
                if (match(_tokens[i]) && _tokens[i].RevokedAt is null)
                    _tokens[i] = _tokens[i] with { RevokedAt = at };
    }
}

/// <summary>A claims source whose answers a test changes between calls — deactivate, narrow permissions.</summary>
public sealed class FakeUserTokenClaimsSource : IUserTokenClaimsSource
{
    private readonly Dictionary<string, string[]?> _permissions = new();

    /// <summary>Sets the subject's permissions; <c>null</c> refuses the subject (deactivated).</summary>
    public void Set(string subject, params string[]? permissions) => _permissions[subject] = permissions;

    public Task<IReadOnlyCollection<Claim>?> GetClaimsAsync(string subject, CancellationToken ct)
    {
        if (!_permissions.TryGetValue(subject, out var perms) || perms is null)
            return Task.FromResult<IReadOnlyCollection<Claim>?>(null);
        var claims = new List<Claim> { new("sub", subject) };
        claims.AddRange(perms.Select(p => IyuIdentityClaims.Permission(p)));
        return Task.FromResult<IReadOnlyCollection<Claim>?>(claims);
    }
}
