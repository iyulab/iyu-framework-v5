namespace Iyu.Core.Attachments;

/// <summary>Pluggable byte backend (Azure Blob, NAS, S3, local). Metadata is owned elsewhere; this stores/serves raw bytes by key.</summary>
public interface IAttachmentStorage
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="storageKey"/>. Returns the effective key.</summary>
    Task<string> SaveAsync(Stream content, string storageKey, string? contentType, CancellationToken ct = default);

    /// <summary>Opens a read stream for the object at <paramref name="storageKey"/>, or <c>null</c> if no
    /// object is stored there.
    /// <para>Absence is a <em>normal</em> state of this contract, not a fault: a key can be deleted while a
    /// still-valid access token is in flight, an orphan sweep can reclaim it, or two deletes can race.
    /// Implementations must therefore normalise their backend's own not-found signal — a filesystem
    /// exception, a 404 response, a missing dictionary entry — into <c>null</c>, so that callers can map
    /// absence to a not-found answer without knowing which backend they are talking to.</para>
    /// <para>Detect absence <em>before</em> returning: a caller that has already begun writing a response
    /// cannot recover from a not-found discovered mid-stream.</para></summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);

    /// <summary>Deletes the object at <paramref name="storageKey"/> (no-op if absent).</summary>
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
