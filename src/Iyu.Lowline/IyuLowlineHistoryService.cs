using System.Collections.Concurrent;
using Lowline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>A history record the engine refused: which one, so the host can find it in its own data.</summary>
/// <param name="Form">The form the record belongs to.</param>
/// <param name="Space">The memory space the record belongs to.</param>
/// <param name="RecordId">The record's id in the history source.</param>
/// <remarks>
/// It names the record and nothing of what it held: the engine's own refusal message can quote a stored value, so it is
/// neither reported nor logged. To see why a record was refused, read it from the source by this id and compare its values
/// with the form's definition.
/// </remarks>
public sealed record IyuLowlineRefusal(string Form, string Space, string RecordId);

/// <summary>What one history pass did.</summary>
/// <param name="Learned">How many records the engine learned, across all scopes.</param>
/// <param name="Refused">How many records the engine refused (they are not tried again until the host restarts).</param>
/// <param name="Skipped">Scopes the pass left alone, and why — for the host's log.</param>
public sealed record IyuLowlineHistoryReport(int Learned, int Refused, IReadOnlyList<string> Skipped)
{
    /// <summary>How many refused records a report (and one warning) names; the rest are only counted.</summary>
    public const int RefusalsListedLimit = 50;

    /// <summary>
    /// The first <see cref="RefusalsListedLimit"/> records refused in this pass, in the order they were met.
    /// <see cref="Refused"/> counts them all, so a longer count than this list means the rest were not named.
    /// </summary>
    public IReadOnlyList<IyuLowlineRefusal> Refusals { get; init; } = [];
}

/// <summary>
/// Brings the records an application held before it began using Lowline to the engine: for each space an
/// <see cref="ILowlineHistorySource"/> names, it learns the history records the store does not hold yet, a batch at a time.
/// </summary>
/// <remarks>
/// Records are learned as typed by a person with no suggestion shown (see <see cref="ILowlineHistorySource"/> for why that is
/// the application's declaration to make). A pass learns at most <see cref="IyuLowlineOptions.HistoryBatchSize"/> records per
/// scope, so a large history arrives over several ticks instead of in one long call. A record the engine refuses is counted,
/// named by its id in the report (<see cref="IyuLowlineHistoryReport.Refusals"/>) and in a warning, and the rest of the batch
/// goes on; it is not tried again until the host restarts, so one bad row cannot take the front of every batch.
/// </remarks>
public sealed class IyuLowlineHistoryService(
    IServiceScopeFactory scopes,
    LowlineForms forms,
    ILowlineRecordIndex index,
    IOptions<IyuLowlineOptions> options,
    ILogger<IyuLowlineHistoryService> logger)
{
    private readonly ConcurrentDictionary<(string Form, string Space), ConcurrentDictionary<string, byte>> _refused = new();

    /// <summary>Runs one pass over every space of every form that has a history source.</summary>
    public async Task<IyuLowlineHistoryReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var learned = 0;
        var refused = 0;
        var refusals = new List<IyuLowlineRefusal>();
        var skipped = new List<string>();

        foreach (var form in settings.Engine.Forms.Keys)
        {
            await using var services = scopes.CreateAsyncScope();
            var source = services.ServiceProvider.GetServices<ILowlineHistorySource>()
                .FirstOrDefault(s => string.Equals(s.Form, form, StringComparison.Ordinal));
            if (source is null) continue;

            IReadOnlyList<string> spaces;
            try
            {
                spaces = await source.ListHistorySpacesAsync(cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                skipped.Add($"{form}: could not list spaces ({failure.GetType().Name})");
                logger.LogError(failure, "Lowline history could not list the spaces of form {Form}.", form);
                continue;
            }

            foreach (var space in spaces)
            {
                // One space that cannot be read must not keep the others from being learned: the worker would meet it first at
                // every tick and never get past it.
                try
                {
                    var (done, turnedAway) = await LearnSpaceAsync(source, new LowlineScope(form, space), settings, cancellationToken);
                    learned += done;
                    refused += turnedAway.Count;
                    foreach (var id in turnedAway.Take(IyuLowlineHistoryReport.RefusalsListedLimit - refusals.Count))
                        refusals.Add(new IyuLowlineRefusal(form, space, id));
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    skipped.Add($"{form}/{space}: failed ({failure.GetType().Name})");
                    logger.LogError(
                        failure, "Lowline history could not learn form {Form}, space {Space}; the other spaces were still learned.",
                        form, space);
                }
            }
        }

        return new IyuLowlineHistoryReport(learned, refused, skipped) { Refusals = refusals };
    }

    private async Task<(int Learned, List<string> Refused)> LearnSpaceAsync(
        ILowlineHistorySource source, LowlineScope scope, IyuLowlineOptions settings, CancellationToken cancellationToken)
    {
        var turnedAway = _refused.GetOrAdd((scope.Form, scope.Space), _ => new ConcurrentDictionary<string, byte>());

        var held = (await index.ListRecordIdsAsync(scope, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var keys = await source.ListHistoryKeysAsync(scope.Space, cancellationToken);
        var next = keys
            .Where(id => !held.Contains(id) && !turnedAway.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .Take(settings.HistoryBatchSize)
            .ToList();
        if (next.Count == 0) return (0, []);

        var learned = 0;
        var refused = new List<string>();
        foreach (var record in await source.ReadHistoryAsync(scope.Space, next, cancellationToken))
        {
            // Only what was asked for: a source that answers with more must not widen the batch.
            if (!next.Contains(record.Id, StringComparer.Ordinal)) continue;

            try
            {
                await forms.SettleAsync(
                    scope, record.Id, record.Values, draft: null, SettledBy.Person, new Attestation(), cancellationToken);
                learned++;
            }
            catch (ArgumentException)
            {
                // The engine refused the stored values and learned nothing. Only the id is kept: the message can quote a value.
                turnedAway[record.Id] = 0;
                refused.Add(record.Id);
            }
        }

        if (refused.Count > 0)
        {
            var named = refused.Take(IyuLowlineHistoryReport.RefusalsListedLimit).ToList();
            var more = refused.Count - named.Count;
            logger.LogWarning(
                "Lowline history: the engine refused {Refused} record(s) of form {Form}, space {Space}: {RecordIds}{More}; they are skipped until the host restarts.",
                refused.Count, scope.Form, scope.Space, string.Join(", ", named), more > 0 ? $" and {more} more" : string.Empty);
        }
        return (learned, refused);
    }
}

/// <summary>Runs <see cref="IyuLowlineHistoryService"/> every <see cref="IyuLowlineOptions.HistoryInterval"/>.</summary>
internal sealed class IyuLowlineHistoryWorker(
    IyuLowlineHistoryService history,
    IOptions<IyuLowlineOptions> options,
    TimeProvider time,
    ILogger<IyuLowlineHistoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.HistoryInterval;
        if (interval <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(interval, time);
        logger.LogDebug("Lowline history worker started; a pass runs every {Interval}.", interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var report = await history.RunOnceAsync(stoppingToken);
                    logger.LogDebug(
                        "Lowline history pass finished: {Learned} learned, {Refused} refused, {Skipped} scope(s) skipped.",
                        report.Learned, report.Refused, report.Skipped.Count);
                    if (report.Learned > 0)
                        logger.LogInformation("Lowline history learned {Count} record(s) from before the installation.", report.Learned);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    logger.LogError(failure, "Lowline history failed; it will run again in {Interval}.", interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
