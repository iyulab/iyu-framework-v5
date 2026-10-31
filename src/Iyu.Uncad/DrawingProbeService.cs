namespace Iyu.Uncad;

/// <summary>
/// The outcome of reading one drawing. A failure is reported in the result, not thrown: a drawing is untrusted input.
/// </summary>
/// <param name="Ok">Whether a <see cref="Probe"/> was read.</param>
/// <param name="Probe">What was read; <c>null</c> when <see cref="Ok"/> is <c>false</c>.</param>
/// <param name="Failure">Why nothing was read; <c>null</c> when <see cref="Ok"/> is <c>true</c>.</param>
/// <param name="Message">A description of the failure for a log; <c>null</c> when <see cref="Ok"/> is <c>true</c>.</param>
/// <param name="Warnings">The warning lines the program wrote to standard error (see <see cref="UncadRunResult.Warnings"/>).</param>
/// <param name="MarksFailed">
/// <c>includeMarks</c> was asked for but reading the layer and color of each dimension failed, so those fields of
/// <see cref="Probe"/> are empty. The dimension entries themselves are valid.
/// </param>
public sealed record DrawingProbeResult(
    bool Ok,
    DrawingProbe? Probe,
    UncadFailure? Failure,
    string? Message,
    IReadOnlyList<string> Warnings,
    bool MarksFailed = false);

/// <summary>
/// Reads a drawing through <c>uncad summarize</c> and turns its JSON into a <see cref="DrawingProbe"/>.
/// </summary>
/// <remarks>
/// It reads and nothing else: what to make of the dimensions and number candidates is the caller's. It runs the program through
/// an <see cref="IUncadRunner"/>, so the process limits and the license boundary described there apply.
/// </remarks>
public sealed class DrawingProbeService(IUncadRunner runner)
{
    private static readonly string[] MarkArgs = ["--type", "DIMENSION", "--detail", "--limit", "100000"];

    /// <summary>Reads one drawing.</summary>
    /// <param name="drawingPath">A <c>.dwg</c> or <c>.dxf</c> file.</param>
    /// <param name="includeMarks">
    /// Also read the layer and effective color of each dimension. This runs the program a second time and its output is much
    /// larger, so ask for it only when it is needed.
    /// </param>
    /// <param name="cancellationToken">Cancelling it throws <see cref="OperationCanceledException"/>.</param>
    public async Task<DrawingProbeResult> ProbeAsync(
        string drawingPath,
        bool includeMarks = false,
        CancellationToken cancellationToken = default)
    {
        var summary = await runner.RunAsync("summarize", drawingPath, [], cancellationToken);
        if (!summary.Ok)
            return new DrawingProbeResult(false, null, summary.Failure, summary.Message, summary.Warnings);

        string? detailJson = null;
        var marksFailed = false;
        var warnings = summary.Warnings;
        if (includeMarks)
        {
            var detail = await runner.RunAsync("summarize", drawingPath, MarkArgs, cancellationToken);
            if (detail.Ok) detailJson = detail.Json;
            else marksFailed = true;
        }

        try
        {
            return new DrawingProbeResult(true, UncadSummaryParser.Parse(summary.Json!, detailJson), null, null, warnings, marksFailed);
        }
        catch (FormatException e)
        {
            // The summary was read and only the second run's output is broken: keep the dimension entries.
            if (detailJson is not null)
            {
                try
                {
                    return new DrawingProbeResult(true, UncadSummaryParser.Parse(summary.Json!), null, null, warnings, MarksFailed: true);
                }
                catch (FormatException)
                {
                    // The summary itself is broken; reported below.
                }
            }

            return new DrawingProbeResult(false, null, UncadFailure.InvalidOutput, "The uncad output could not be read: " + e.Message, warnings);
        }
    }
}
