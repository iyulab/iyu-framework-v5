namespace Iyu.Core.Attachments;

/// <summary>Pluggable byte backend (Azure Blob, NAS, S3, local). Metadata is owned elsewhere; this stores/serves raw bytes by key.</summary>
public interface IAttachmentStorage
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="storageKey"/>. Returns the effective key.</summary>
    Task<string> SaveAsync(Stream content, string storageKey, string? contentType, CancellationToken ct = default);

    /// <summary>Opens a read stream for the object at <paramref name="storageKey"/>.</summary>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default);

    /// <summary>Deletes the object at <paramref name="storageKey"/> (no-op if absent).</summary>
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
