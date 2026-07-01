using Iyu.Core.Entities;
namespace Iyu.Core.Identity;

/// <summary>Default base class an mdd-generated User entity may inherit (via @inherits) to satisfy IUser.</summary>
public abstract class IyuUser : IyuEntity, IUser
{
    public string Username { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
}
