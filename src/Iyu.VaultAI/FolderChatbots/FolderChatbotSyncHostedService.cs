using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// Runs the folder chatbot check once at start, then every <see cref="VaultAIFolderChatbotOptions.RescanMinutes"/> minutes, and straight
/// away when <see cref="RequestSync"/> is called (a call during a check makes one more check run after it). A check that throws is logged
/// and tried again after a short pause; the loop never ends on its own error.
/// </summary>
internal sealed class FolderChatbotSyncHostedService : BackgroundService
{
    private readonly FolderChatbotSyncService _service;
    private readonly ILogger<FolderChatbotSyncHostedService> _log;
    private readonly SemaphoreSlim _signal = new(0, 1);

    public FolderChatbotSyncHostedService(
        FolderChatbotSyncService service, IOptions<VaultAIFolderChatbotOptions> options, ILogger<FolderChatbotSyncHostedService> log)
    {
        _service = service;
        _log = log;
        Interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.RescanMinutes));
    }

    /// <summary>The pause between checks. Settable for tests only.</summary>
    internal TimeSpan Interval { get; set; }

    /// <summary>The pause after a check that threw. Settable for tests only.</summary>
    internal TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many checks have finished (completed or thrown). For tests and diagnostics.</summary>
    internal int Checks => Volatile.Read(ref _checks);

    private int _checks;

    /// <summary>Asks for a check now instead of at the next interval.</summary>
    public void RequestSync()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* one request is already waiting */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();   // the host's start is not held up
        var pause = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (pause > TimeSpan.Zero) await _signal.WaitAsync(pause, stoppingToken).ConfigureAwait(false);
                var report = await _service.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                Interlocked.Increment(ref _checks);
                if (report.GuardWarning is not null)
                    _log.LogWarning("Folder chatbot check ended with a warning: {Warning}", report.GuardWarning);
                pause = Interval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _checks);
                _log.LogError(ex, "A folder chatbot check failed unexpectedly - trying again in {Pause}", ErrorBackoff);
                pause = ErrorBackoff;
            }
        }
    }
}
