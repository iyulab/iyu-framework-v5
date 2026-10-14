namespace Iyu.MainServer.Identity;

public sealed record CreateServiceClientRequest(string DisplayName, IReadOnlyList<string> Permissions, DateTimeOffset? ExpiresAt);
public sealed record UpdateServiceClientPermissionsRequest(IReadOnlyList<string> Permissions);

/// <summary>An administrator's issuance on a service account's behalf: the account the client is held under, and the rest of a create.</summary>
public sealed record IssueServiceClientRequest(Guid OwnerUserId, string DisplayName, IReadOnlyList<string> Permissions, DateTimeOffset? ExpiresAt);
