namespace Iyu.MainServer.Identity;

public sealed record CreateResult(bool Ok, string? Error, IReadOnlyList<string> Exceeding,
    string? ClientId, string? PlaintextSecret, Guid Id);
public sealed record RotateResult(bool Ok, string? PlaintextSecret);

/// <summary>Owner-scoped service-client lifecycle: create (subset ⊆ owner), rotate, revoke.</summary>
public sealed class ServiceClientService
{
    private readonly IIdentityStore _store;
    private readonly IServiceClientStore _writes;

    public ServiceClientService(IIdentityStore store, IServiceClientStore writes)
    {
        _store = store; _writes = writes;
    }

    public async Task<CreateResult> CreateAsync(Guid ownerUserId, string displayName,
        IReadOnlyList<string> requestedPermissions, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var ownerPerms = await _store.GetUserPermissionsAsync(ownerUserId, ct);
        var exceeding = PermissionScope.Exceeding(requestedPermissions, ownerPerms);
        if (exceeding.Count > 0)
            return new(false, "permissions_exceed_owner", exceeding, null, null, Guid.Empty);

        var effective = PermissionScope.Effective(requestedPermissions, ownerPerms);
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        var id = await _writes.InsertAsync(clientId, hash, displayName, ownerUserId, expiresAt, effective, ct);
        return new(true, null, Array.Empty<string>(), clientId, secret, id);
    }

    public async Task<RotateResult> RotateAsync(Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var (_, secret, hash) = ServiceClientSecrets.Generate();
        var ok = await _writes.UpdateSecretAsync(id, ownerUserId, hash, ct);
        return ok ? new(true, secret) : new(false, null);
    }

    public Task<bool> RevokeAsync(Guid id, Guid ownerUserId, CancellationToken ct) =>
        _writes.DeactivateAsync(id, ownerUserId, ct);

    /// <summary>
    /// The owner's service clients, revoked ones included and marked as such.
    /// </summary>
    /// <remarks>
    /// Create, rotate and revoke all key on an <c>id</c> that was returned exactly once, at
    /// issuance. Without a way back to that id, an owner who lost the issuing response cannot
    /// retire a credential even after its secret leaks — the three operations above are only
    /// conditionally usable until this one exists.
    /// </remarks>
    public Task<IReadOnlyList<ServiceClientSummary>> ListAsync(Guid ownerUserId, CancellationToken ct) =>
        _store.ListServiceClientsByOwnerAsync(ownerUserId, ct);
}
