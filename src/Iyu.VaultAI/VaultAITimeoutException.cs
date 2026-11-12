namespace Iyu.VaultAI;

/// <summary>
/// A call to the vault-ai server did not finish within its time limit — the module's own limit
/// (<see cref="VaultAISettings.RequestTimeout"/>) or the one the caller passed to the overload taking a
/// <see cref="TimeSpan"/>. It is not an <see cref="OperationCanceledException"/>: the caller's own cancellation still
/// surfaces as one, so a catch block can tell "I stopped it" from "it took too long".
/// </summary>
public sealed class VaultAITimeoutException : TimeoutException
{
    public VaultAITimeoutException(TimeSpan timeout)
        : base($"The vault-ai call did not finish within {timeout}.")
    {
        Timeout = timeout;
    }

    /// <summary>The limit that was exceeded.</summary>
    public TimeSpan Timeout { get; }
}
