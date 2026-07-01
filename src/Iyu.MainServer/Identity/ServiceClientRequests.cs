namespace Iyu.MainServer.Identity;

public sealed record CreateServiceClientRequest(string DisplayName, IReadOnlyList<string> Permissions, DateTimeOffset? ExpiresAt);
