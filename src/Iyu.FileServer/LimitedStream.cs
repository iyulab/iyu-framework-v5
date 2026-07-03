namespace Iyu.FileServer;

/// <summary>
/// Read-only wrapper that enforces a byte ceiling on the bytes actually read from
/// <paramref name="inner"/>, regardless of what the caller's Content-Length header claims.
/// Defends against chunked-transfer or lying-header uploads that would otherwise stream
/// unbounded bytes into storage. Throws <see cref="PayloadTooLargeException"/> once more than
/// <c>maxBytes</c> have been read.
/// </summary>
internal sealed class LimitedStream(Stream inner, long maxBytes) : Stream
{
    private long _totalRead;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Account(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Account(int bytesRead)
    {
        _totalRead += bytesRead;
        if (_totalRead > maxBytes)
            throw new PayloadTooLargeException(maxBytes);
    }
}
