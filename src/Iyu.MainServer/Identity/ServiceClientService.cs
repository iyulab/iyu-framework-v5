namespace Iyu.MainServer.Identity;

public sealed record CreateResult(bool Ok, string? Error, IReadOnlyList<string> Exceeding,
    string? ClientId, string? PlaintextSecret, Guid Id);
public sealed record RotateResult(bool Ok, string? PlaintextSecret);
public sealed record UpdatePermissionsResult(bool Ok, string? Error, IReadOnlyList<string> Exceeding);

/// <summary>Service-client lifecycle: the owner's create (subset ⊆ owner), rotate and revoke, and an administrator's
/// listing and revocation across owners.</summary>
/// <remarks>What the owner holds is what <see cref="IUserClaimsSource"/> answers for them; an owner the source
/// refuses holds nothing, so every requested code exceeds it.</remarks>
public sealed class ServiceClientService
{
    private readonly IServiceClientStore _store;
    private readonly IUserClaimsSource _owners;
    private readonly IdentityTokenOptions _opts;
    private readonly TimeProvider _clock;
    private readonly PermissionHierarchy _hierarchy;

    public ServiceClientService(IServiceClientStore store, IUserClaimsSource owners, IdentityTokenOptions opts,
        TimeProvider clock, PermissionHierarchy hierarchy)
    {
        _store = store; _owners = owners; _opts = opts; _clock = clock; _hierarchy = hierarchy;
    }

    /// <summary>An owner's own client: held under, and issued by, <paramref name="ownerUserId"/>.</summary>
    public Task<CreateResult> CreateAsync(Guid ownerUserId, string displayName,
        IReadOnlyList<string> requestedPermissions, DateTimeOffset? expiresAt, CancellationToken ct)
        => IssueAsync(ownerUserId, ownerUserId, displayName, requestedPermissions, expiresAt, ct);

    /// <summary>
    /// A client held under <paramref name="ownerUserId"/> and issued by someone else — an administrator, on a service
    /// account's behalf. The ceiling is the owner's permissions, not the issuer's; whether the issuer may act for the
    /// owner is the caller's to have asked (<see cref="IServiceAccounts"/>).
    /// </summary>
    public Task<CreateResult> IssueForAsync(Guid ownerUserId, Guid issuedBy, string displayName,
        IReadOnlyList<string> requestedPermissions, DateTimeOffset? expiresAt, CancellationToken ct)
        => IssueAsync(ownerUserId, issuedBy, displayName, requestedPermissions, expiresAt, ct);

    private async Task<CreateResult> IssueAsync(Guid ownerUserId, Guid issuedBy, string displayName,
        IReadOnlyList<string> requestedPermissions, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var ownerPerms = await ServiceClientOwner.PermissionsAsync(_owners, _opts, ownerUserId, ct) ?? [];
        var exceeding = PermissionScope.Exceeding(requestedPermissions, ownerPerms, _hierarchy);
        if (exceeding.Count > 0)
            return new(false, "permissions_exceed_owner", exceeding, null, null, Guid.Empty);

        var effective = PermissionScope.Effective(requestedPermissions, ownerPerms, _hierarchy);
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        var id = await _store.InsertAsync(clientId, hash, displayName, ownerUserId, issuedBy, expiresAt, effective, ct);
        return new(true, null, Array.Empty<string>(), clientId, secret, id);
    }

    public async Task<RotateResult> RotateAsync(Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var (_, secret, hash) = ServiceClientSecrets.Generate();
        var ok = await _store.UpdateSecretAsync(id, ownerUserId, hash, _clock.GetUtcNow(), ct);
        return ok ? new(true, secret) : new(false, null);
    }

    public Task<bool> RevokeAsync(Guid id, Guid ownerUserId, CancellationToken ct) =>
        _store.DeactivateAsync(id, ownerUserId, ct);

    /// <summary>
    /// Replaces a service client's permission grant, subject to the same owner ⊇ effective rule
    /// as <see cref="CreateAsync"/>. The secret is untouched — this is the axis <c>rotate</c> does
    /// not cover, for when only the scope needs to change, not the credential itself.
    /// </summary>
    public async Task<UpdatePermissionsResult> UpdatePermissionsAsync(Guid id, Guid ownerUserId,
        IReadOnlyList<string> requestedPermissions, CancellationToken ct)
    {
        var ownerPerms = await ServiceClientOwner.PermissionsAsync(_owners, _opts, ownerUserId, ct) ?? [];
        var exceeding = PermissionScope.Exceeding(requestedPermissions, ownerPerms, _hierarchy);
        if (exceeding.Count > 0)
            return new(false, "permissions_exceed_owner", exceeding);

        var effective = PermissionScope.Effective(requestedPermissions, ownerPerms, _hierarchy);
        var ok = await _store.UpdatePermissionsAsync(id, ownerUserId, effective, ct);
        return ok ? new(true, null, Array.Empty<string>()) : new(false, "not_found", Array.Empty<string>());
    }

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

    /// <summary>Every owner's service clients, revoked ones included — an administrator's view.</summary>
    /// <remarks>
    /// A password reset and a signed-out session do not end the credentials an account issued; finding them is the
    /// next step of answering a compromised account, and the owner is the one person who cannot be asked.
    /// </remarks>
    public Task<IReadOnlyList<ServiceClientSummary>> ListAllAsync(CancellationToken ct) =>
        _store.ListServiceClientsAsync(ct);

    /// <summary>The account a client is held under, or <c>null</c> when there is no such client.</summary>
    /// <remarks>
    /// Read from the full listing (<see cref="IServiceClientStore.ListServiceClientsAsync"/>) — the store has no lookup by
    /// id, and an administrator's rotate or re-scope is rare beside the listing it already reads in full.
    /// </remarks>
    public async Task<Guid?> OwnerOfAsync(Guid id, CancellationToken ct)
        => (await _store.ListServiceClientsAsync(ct)).FirstOrDefault(c => c.Id == id)?.OwnerUserId;

    /// <summary>Revokes a service client whoever owns it — an administrator's revocation.</summary>
    public Task<bool> RevokeAnyAsync(Guid id, CancellationToken ct) => _store.DeactivateAsync(id, ct);

    /// <summary>Revokes every active service client of <paramref name="ownerUserId"/>; returns how many.</summary>
    public Task<int> RevokeAllOfOwnerAsync(Guid ownerUserId, CancellationToken ct) =>
        _store.DeactivateByOwnerAsync(ownerUserId, ct);
}
