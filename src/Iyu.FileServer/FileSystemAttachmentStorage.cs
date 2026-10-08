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
        var staged = await StageAsync(content, path, ct).ConfigureAwait(false);
        File.Move(staged, path, overwrite: true);
        return storageKey;
    }

    public async Task<bool> TryCreateAsync(Stream content, string storageKey, string? contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Resolve(storageKey);
        // Cheap early answer for the common case; the move below is what decides a race.
        if (File.Exists(path)) return false;

        var staged = await StageAsync(content, path, ct).ConfigureAwait(false);
        try
        {
            File.Move(staged, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            TryDelete(staged);
            return false;
        }
    }

    /// <summary>The stored file itself — no copy. Disposing the result leaves it in place.</summary>
    public Task<LocalAttachmentFile?> OpenLocalFileAsync(string storageKey, CancellationToken ct = default)
    {
        var path = Resolve(storageKey);
        return Task.FromResult(File.Exists(path) ? LocalAttachmentFile.Stored(path) : null);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a temporary file beside <paramref name="path"/> and returns its path,
    /// for the caller to move into place.
    /// </summary>
    /// <remarks>
    /// Writing beside the object and moving it is what keeps a write from damaging the object: a reader never
    /// sees a half-written file, and a write that fails (too large, cancelled, the connection dropped) leaves the
    /// object that was there untouched — before, the bytes went into the object's own file and a failure deleted
    /// it. Same directory, so the move is a rename on one volume.
    /// </remarks>
    private static async Task<string> StageAsync(Stream content, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            await using var fs = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
        return staged;
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        var path = Resolve(storageKey);
        try
        {
            // Opening and catching, rather than File.Exists then opening, keeps this free of a
            // check-then-act race: a concurrent delete between the two would resurrect the very
            // exception this normalisation exists to absorb.
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult<Stream?>(stream);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            // Keys are path-shaped ("2026/07/abc"), so an absent object can present as an absent
            // parent directory rather than an absent file.
            return Task.FromResult<Stream?>(null);
        }
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
