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

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream(Objects[storageKey]));

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        Objects.TryRemove(storageKey, out _);
        return Task.CompletedTask;
    }
}
