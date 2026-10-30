using Lowline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>What one recovery pass did.</summary>
/// <param name="Forgotten">How many records were forgotten, across all scopes.</param>
/// <param name="Skipped">Scopes the pass left alone, and why — for the host's log.</param>
public sealed record IyuLowlineRecoveryReport(int Forgotten, IReadOnlyList<string> Skipped);

/// <summary>
/// Forgets the records of sources that no longer exist: it compares the ids a scope holds with the source keys the
/// form's <see cref="ILowlineFormHook"/> lists, and has the store and the running engine forget the ones with no source.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of "delete means forget". It does not watch deletes, so it catches every way a source can go —
/// a database cascade, a bulk delete, a delete from another system — at the cost of a delay of up to
/// <see cref="IyuLowlineOptions.RecoveryInterval"/>.
/// </para>
/// <para>
/// <b>Order is the guard against forgetting a record that is only new.</b> The scope's ids are read <em>first</em>, the
/// source keys <em>after</em>: a record settled in between is in the keys but not in the ids, so it cannot be mistaken
/// for one whose source went. Forgetting cannot be undone, so a pass that would forget more than
/// <see cref="IyuLowlineOptions.MaxForgetFraction"/> of a scope — most likely a source listing that came back short —
/// forgets nothing and is reported instead.
/// </para>
/// </remarks>
public sealed class IyuLowlineRecoveryService(
    IServiceScopeFactory scopes,
    LowlineForms forms,
    ILowlineRecordIndex index,
    IOptions<IyuLowlineOptions> options,
    ILogger<IyuLowlineRecoveryService> logger)
{
    // A scope with fewer records than this is not guarded by the fraction: forgetting one of two is half, and unremarkable.
    private const int GuardedFrom = 10;

    /// <summary>Runs one pass over every scope the store holds records for.</summary>
    public async Task<IyuLowlineRecoveryReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var forgotten = 0;
        var skipped = new List<string>();

        foreach (var scope in await index.ListScopesAsync(cancellationToken))
        {
            // One scope that cannot be recovered - its hook throws, its source is unreachable - must not keep the others
            // from being recovered: the worker would meet the same scope first at every tick and never get past it.
            try
            {
                forgotten += await RecoverScopeAsync(scope, settings, skipped, cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                skipped.Add($"{scope.Form}/{scope.Space}: failed ({failure.GetType().Name})");
                logger.LogError(
                    failure, "Lowline recovery could not recover form {Form}, space {Space}; the other scopes were still recovered.",
                    scope.Form, scope.Space);
            }
        }

        return new IyuLowlineRecoveryReport(forgotten, skipped);
    }

    private async Task<int> RecoverScopeAsync(
        LowlineScope scope, IyuLowlineOptions settings, List<string> skipped, CancellationToken cancellationToken)
    {
        if (!settings.Engine.Forms.ContainsKey(scope.Form)) return 0;

        await using var services = scopes.CreateAsyncScope();
        var hook = services.ServiceProvider.GetServices<ILowlineFormHook>()
            .FirstOrDefault(h => string.Equals(h.Form, scope.Form, StringComparison.Ordinal));
        if (hook is null) return 0;

        // The ids first, the source keys after (see the remarks).
        var held = await index.ListRecordIdsAsync(scope, cancellationToken);
        var keys = await hook.ListSourceKeysAsync(scope.Space, cancellationToken);
        if (keys is null) return 0;

        var gone = held.Where(id => !keys.Contains(id)).ToList();
        if (gone.Count == 0) return 0;

        if (held.Count >= GuardedFrom && gone.Count > held.Count * settings.MaxForgetFraction)
        {
            skipped.Add($"{scope.Form}/{scope.Space}: {gone.Count} of {held.Count} records have no source");
            logger.LogError(
                "Lowline recovery left form {Form}, space {Space} alone: {Gone} of its {Held} records have no source, which is more than MaxForgetFraction allows. Check the source listing.",
                scope.Form, scope.Space, gone.Count, held.Count);
            return 0;
        }

        // ForgetAsync takes the record out of the store and has the running engine forget it, so the engine stops
        // answering from it at once; the thresholds are chosen again in the background if they no longer stand.
        var forgotten = 0;
        foreach (var id in gone)
        {
            if (await forms.ForgetAsync(scope, id, cancellationToken)) forgotten++;
        }

        return forgotten;
    }
}

/// <summary>Runs <see cref="IyuLowlineRecoveryService"/> every <see cref="IyuLowlineOptions.RecoveryInterval"/>.</summary>
internal sealed class IyuLowlineRecoveryWorker(
    IyuLowlineRecoveryService recovery,
    IOptions<IyuLowlineOptions> options,
    TimeProvider time,
    ILogger<IyuLowlineRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.RecoveryInterval;
        if (interval <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(interval, time);
        // Logged once the timer exists: its period counts from its creation, and a test (or an operator reading the log) that
        // waits for this line may then move the clock.
        logger.LogDebug("Lowline recovery worker started; a pass runs every {Interval}.", interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var report = await recovery.RunOnceAsync(stoppingToken);
                    logger.LogDebug("Lowline recovery pass finished: {Forgotten} forgotten, {Skipped} scope(s) skipped.", report.Forgotten, report.Skipped.Count);
                    if (report.Forgotten > 0)
                        logger.LogInformation("Lowline recovery forgot {Count} record(s) whose source is gone.", report.Forgotten);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    // A pass that fails is tried again at the next tick; the host keeps running.
                    logger.LogError(failure, "Lowline recovery failed; it will run again in {Interval}.", interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
