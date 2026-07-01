namespace Iyu.Core.Identity;

/// <summary>A machine credential owned by a user, authenticating via client_credentials.</summary>
public interface IServiceClient
{
    Guid Id { get; }
    string ClientId { get; }
    string SecretHash { get; }
    string DisplayName { get; }
    Guid OwnerUserId { get; }
    bool IsActive { get; }
    DateTimeOffset? ExpiresAt { get; }
    DateTimeOffset? LastUsedAt { get; }
}
