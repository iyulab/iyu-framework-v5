using Iyu.Core.Entities;
namespace Iyu.Core.Identity;

public abstract class IyuServiceClient : IyuEntity, IServiceClient
{
    public string ClientId { get; set; } = default!;
    public string SecretHash { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public Guid OwnerUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
