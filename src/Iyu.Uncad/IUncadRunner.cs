namespace Iyu.Uncad;

/// <summary>Why a run of the program ended without a result.</summary>
public enum UncadFailure
{
    /// <summary>The executable does not exist at the configured path (or no path is configured).</summary>
    NotConfigured,

    /// <summary>The input file does not exist or is not a <c>.dwg</c> or <c>.dxf</c> file.</summary>
    InvalidInput,

    /// <summary>The run exceeded the time limit and its process tree was ended.</summary>
    Timeout,

    /// <summary>The output exceeded the size limit and the process tree was ended.</summary>
    OutputTooLarge,

    /// <summary>The program exited with a code other than zero (a crash of its parser included).</summary>
    NonZeroExit,

    /// <summary>The process could not be started.</summary>
    StartFailed,

    /// <summary>The program exited with code zero but its output could not be read as the JSON it was expected to be.</summary>
    InvalidOutput,
}

/// <summary>The outcome of one run of the program.</summary>
/// <param name="Ok">Whether the program exited with code zero.</param>
/// <param name="Json">The standard output of a successful run (one JSON document); <c>null</c> when the run failed.</param>
/// <param name="Failure">Why the run failed; <c>null</c> when it did not.</param>
/// <param name="Message">A description of the failure for a log; <c>null</c> when the run did not fail.</param>
/// <param name="Warnings">
/// The lines of standard error that begin with <c>warning:</c>, kept even when the exit code is zero: a run can succeed with
/// warnings, and an entity the program could not decode is then missing from its output.
/// </param>
/// <param name="Elapsed">How long the run took.</param>
public sealed record UncadRunResult(
    bool Ok,
    string? Json,
    UncadFailure? Failure,
    string? Message,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed)
{
    /// <summary>A failed run.</summary>
    public static UncadRunResult Fail(UncadFailure failure, string message, TimeSpan elapsed, IReadOnlyList<string>? warnings = null)
        => new(false, null, failure, message, warnings ?? [], elapsed);
}

/// <summary>Runs the <c>uncad</c> command-line program as a separate process.</summary>
public interface IUncadRunner
{
    /// <summary>
    /// Runs <c>uncad &lt;verb&gt; &lt;drawingPath&gt; &lt;extraArgs&gt;</c> and returns its standard output. The first argument is
    /// a command word such as <c>summarize</c>, not the drawing: <paramref name="drawingPath"/> is taken separately so it can be
    /// checked before anything is started.
    /// </summary>
    /// <remarks>
    /// A failed run is reported in the result, not thrown. Only cancelling <paramref name="cancellationToken"/> throws
    /// (<see cref="OperationCanceledException"/>); the limits of <see cref="IyuUncadOptions"/> end a run with a failure instead.
    /// </remarks>
    Task<UncadRunResult> RunAsync(
        string verb,
        string drawingPath,
        IReadOnlyList<string> extraArgs,
        CancellationToken cancellationToken = default);
}
