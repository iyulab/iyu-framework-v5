namespace Iyu.Core.Attachments;

/// <summary>
/// A stored attachment as a file on the local file system (<see cref="IAttachmentStorage.OpenLocalFileAsync"/>).
/// Read <see cref="Path"/>; dispose when done.
/// </summary>
/// <remarks>
/// Disposing deletes the file only when it is a copy made for the call. When the backend handed out the stored
/// file itself, disposing leaves it in place — it is the object, not a copy of it.
/// </remarks>
public sealed class LocalAttachmentFile : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsFile;
    private int _disposed;

    private LocalAttachmentFile(string path, bool ownsFile)
    {
        Path = path;
        _ownsFile = ownsFile;
    }

    /// <summary>The file's full path. Read it; do not change it.</summary>
    public string Path { get; }

    /// <summary>The stored file itself — disposing leaves it in place.</summary>
    public static LocalAttachmentFile Stored(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new LocalAttachmentFile(path, ownsFile: false);
    }

    /// <summary>A copy made for this call — disposing deletes it.</summary>
    public static LocalAttachmentFile Copy(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new LocalAttachmentFile(path, ownsFile: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_ownsFile) return;
        try { File.Delete(Path); }
        catch (IOException) { }   // still open elsewhere; the temp directory is the operating system's to clean
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
