using Iyu.Core.Entities;

namespace Iyu.Core.Attachments;

/// <summary>Default base an mdd-generated Attachment entity may inherit (via @inherits) to satisfy <see cref="IAttachment"/>. Id/CreatedAt/UpdatedAt come from <see cref="IyuEntity"/>.</summary>
public abstract class IyuAttachment : IyuEntity, IAttachment
{
    public string FileName { get; set; } = default!;
    public string? ContentType { get; set; }
    public long? ByteSize { get; set; }
    public string StorageKey { get; set; } = default!;
    public string? UploadedBy { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
}
