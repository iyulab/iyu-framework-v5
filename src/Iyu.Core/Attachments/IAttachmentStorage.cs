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

    /// <summary>
    /// Writes <paramref name="content"/> under <paramref name="storageKey"/> only if nothing is stored there.
    /// Returns <c>false</c>, having written nothing, when an object already exists.
    /// </summary>
    /// <remarks>
    /// The check and the write are one step in the backend — two concurrent calls for the same key produce
    /// exactly one <c>true</c>. This is what an immutable object needs (a content-addressed key written once,
    /// possibly already being read): a check with <see cref="OpenReadAsync"/> followed by
    /// <see cref="SaveAsync"/> leaves a window in which a second writer replaces the first.
    /// </remarks>
    Task<bool> TryCreateAsync(Stream content, string storageKey, string? contentType, CancellationToken ct = default);

    /// <summary>
    /// The object at <paramref name="storageKey"/> as a file on the local file system, for a tool that opens
    /// a path rather than a stream; <c>null</c> if no object is stored there.
    /// </summary>
    /// <remarks>
    /// <para>The file is the stored object's to read, not the caller's to change: a backend that keeps
    /// objects on local disk hands out the stored file itself. Dispose the result when done — a copy made for
    /// the call is deleted then; the stored file is not.</para>
    /// <para>This default copies the object to a temporary file through <see cref="OpenReadAsync"/>, so every
    /// backend has it; a backend that already keeps a file overrides it to hand that file out without a copy.</para>
    /// </remarks>
    async Task<LocalAttachmentFile?> OpenLocalFileAsync(string storageKey, CancellationToken ct = default)
    {
        await using var source = await OpenReadAsync(storageKey, ct).ConfigureAwait(false);
        if (source is null) return null;

        var path = Path.Combine(Path.GetTempPath(), "iyu-attachment-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        return LocalAttachmentFile.Copy(path);
    }
}
