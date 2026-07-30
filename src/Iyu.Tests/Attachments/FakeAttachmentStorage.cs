using System.Collections.Concurrent;
using Iyu.Core.Attachments;

namespace Iyu.Tests.Attachments;

public sealed class FakeAttachmentStorage : IAttachmentStorage
{
    public readonly ConcurrentDictionary<string, byte[]> Objects = new();

    public async Task<string> SaveAsync(Stream content, string storageKey, string? contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Objects[storageKey] = ms.ToArray();
        return storageKey;
    }

    /// <remarks>Returns <c>null</c> for an absent key, as the contract requires. The indexer this once used
    /// threw <see cref="KeyNotFoundException"/> — a signal no real backend produces — so the double could
    /// not reproduce the absence path and hid a gateway defect behind a green suite.</remarks>
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(Objects.TryGetValue(storageKey, out var bytes) ? new MemoryStream(bytes) : null);

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        Objects.TryRemove(storageKey, out _);
        return Task.CompletedTask;
    }
}
