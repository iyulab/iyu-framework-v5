namespace Iyu.VaultAI;

/// <summary>
/// Runs one call under a time limit and says which side stopped it: the limit expiring becomes a
/// <see cref="VaultAITimeoutException"/>, the caller's own token firing stays the caller's cancellation.
/// </summary>
internal static class VaultAIDeadline
{
    internal static async Task<T> RunAsync<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        // Zero, negative and "infinite" are refused: an unbounded call is the thing the limit exists to prevent.
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The time limit has to be greater than zero.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await call(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new VaultAITimeoutException(timeout);
        }
    }
}
