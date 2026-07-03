namespace Iyu.FileServer;

/// <summary>Thrown by <see cref="LimitedStream"/> when the underlying stream yields more bytes than the configured cap.</summary>
internal sealed class PayloadTooLargeException : Exception
{
    public PayloadTooLargeException(long maxBytes)
        : base($"Stream exceeded the maximum allowed size of {maxBytes} bytes.")
    {
    }
}
