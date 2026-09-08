using Microsoft.Extensions.Logging;

namespace Iyu.Tests.Identity;

/// <summary>
/// Captures formatted log messages so a test can assert what the server recorded, as distinct
/// from what it answered.
/// </summary>
/// <remarks>
/// The token endpoint answers every rejection identically by design, so the only way to pin that
/// the cause is still recorded somewhere is to read the log. Asserting on the response would
/// re-check the indistinguishability, not the diagnostics.
/// </remarks>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public readonly List<string> Messages = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}
