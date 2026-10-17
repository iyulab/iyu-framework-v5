namespace Iyu.Core.Identity;

/// <summary>Core identity contract for a human user. Domain fields (e.g. tenant/enterprise) are added by the consuming app.</summary>
public interface IUser
{
    Guid Id { get; }
    string Username { get; }

    /// <summary>
    /// The stored password hash, or <see langword="null"/> (or empty) for an account with no password — one that signs
    /// in only through an external provider, or a service account that only owns machine credentials. A password check
    /// answers such an account as it answers a name that is not an account.
    /// </summary>
    string? PasswordHash { get; }
    string DisplayName { get; }
    string? Email { get; }
    bool IsActive { get; }
    DateTimeOffset? LastLoginAt { get; }
}
