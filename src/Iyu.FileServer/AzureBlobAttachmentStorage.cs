using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Iyu.Core.Attachments;

namespace Iyu.FileServer;

/// <summary>Azure Blob Storage-backed <see cref="IAttachmentStorage"/>. Container is created lazily on first use (constructing
/// <see cref="BlobContainerClient"/> itself performs no network I/O, so DI registration stays offline/testable).</summary>
public sealed class AzureBlobAttachmentStorage : IAttachmentStorage
{
    private readonly BlobContainerClient _container;
    private bool _containerEnsured;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    public AzureBlobAttachmentStorage(AzureBlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _container = new BlobContainerClient(options.ConnectionString, options.ContainerName);
    }

    public async Task<string> SaveAsync(Stream content, string storageKey, string? contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(storageKey);

        await EnsureContainerAsync(ct).ConfigureAwait(false);
        var blob = _container.GetBlobClient(storageKey);
        var headers = string.IsNullOrEmpty(contentType) ? null : new BlobHttpHeaders { ContentType = contentType };
        await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = headers }, ct).ConfigureAwait(false);
        return storageKey;
    }

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);

        await EnsureContainerAsync(ct).ConfigureAwait(false);
        var blob = _container.GetBlobClient(storageKey);
        try
        {
            return await blob.OpenReadAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Covers both BlobNotFound and ContainerNotFound: either way nothing is stored at this key.
            return null;
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);

        await EnsureContainerAsync(ct).ConfigureAwait(false);
        var blob = _container.GetBlobClient(storageKey);
        await blob.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerEnsured) return;
        await _ensureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_containerEnsured) return;
            await _container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            _containerEnsured = true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }
}
