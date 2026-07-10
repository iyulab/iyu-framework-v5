using Iyu.Core.Attachments;

namespace Iyu.FileServer;

/// <summary>Local filesystem-backed <see cref="IAttachmentStorage"/>. Maps each storage key to a file beneath
/// <see cref="FileSystemOptions.RootPath"/>. Content-type is not persisted — the download path carries it in the
/// signed token, so this backend stores raw bytes only (parity with the blob backend's behaviour).</summary>
public sealed class FileSystemAttachmentStorage : IAttachmentStorage
{
    private readonly string _root;

    public FileSystemAttachmentStorage(FileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("FileSystemOptions.RootPath must be set.", nameof(options));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string storageKey, string? contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Resolve(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        catch
        {
            // Don't leave a truncated/partial object behind on a failed (e.g. too-large / cancelled) upload.
            TryDelete(path);
            throw;
        }
        return storageKey;
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        var path = Resolve(storageKey);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        TryDelete(Resolve(storageKey));   // no-op if absent
        return Task.CompletedTask;
    }

    /// <summary>Maps a storage key to an absolute path and guarantees it stays beneath the root.
    /// Keys are server-authoritative, but this is defence-in-depth for a public byte gateway.</summary>
    private string Resolve(string storageKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        if (storageKey.Contains("..") || IsRootedOnAnyPlatform(storageKey))
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));

        var full = Path.GetFullPath(Path.Combine(_root, storageKey));
        var underRoot = string.Equals(full, _root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!underRoot)
            throw new ArgumentException("Storage key escapes the storage root.", nameof(storageKey));
        return full;
    }

    /// <summary>True if the key is absolute under <em>either</em> Windows or Unix rules. The runtime's
    /// <see cref="Path.IsPathRooted(string)"/> only knows the host OS, so a key that is dangerous on the
    /// deployment target (Windows/IIS) — e.g. a drive-letter or leading-separator path — must be rejected
    /// deterministically even when this runs on a Linux CI host, otherwise the guard's behaviour silently
    /// diverges by OS.</summary>
    private static bool IsRootedOnAnyPlatform(string key) =>
        Path.IsPathRooted(key)                                              // host-OS rooted
        || key[0] is '/' or '\\'                                            // Unix root / Windows leading-separator or UNC
        || (key.Length >= 2 && char.IsAsciiLetter(key[0]) && key[1] == ':'); // Windows drive-letter (e.g. C:)

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
