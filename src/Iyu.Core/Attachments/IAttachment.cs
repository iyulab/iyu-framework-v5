namespace Iyu.Core.Attachments;

/// <summary>Metadata contract for a file attached to some entity. Bytes live in an <see cref="IAttachmentStorage"/> backend.</summary>
public interface IAttachment
{
    Guid Id { get; }
    string FileName { get; }
    string? ContentType { get; }
    long? ByteSize { get; }
    string StorageKey { get; }
    string? UploadedBy { get; }
    DateTimeOffset? UploadedAt { get; }
}
