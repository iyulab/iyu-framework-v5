namespace Iyu.Core.Identity;

/// <summary>Core identity contract for a human user. Domain fields (e.g. tenant/enterprise) are added by the consuming app.</summary>
public interface IUser
{
    Guid Id { get; }
    string Username { get; }
    string PasswordHash { get; }
    string DisplayName { get; }
    string? Email { get; }
    bool IsActive { get; }
    DateTimeOffset? LastLoginAt { get; }
}
