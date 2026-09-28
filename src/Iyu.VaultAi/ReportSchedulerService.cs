using Cronos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Iyu.VaultAi;

/// <summary>
/// cron 예약 기반 AI 리포트 자동 생성기. v3의 JobServiceBase(MinutesRepeatSettings(1)
/// + FireAndForget)를 표준 BackgroundService + PeriodicTimer(1분)로 치환한 것.
/// tick 내부에서 await하므로 리포트 생성이 겹쳐 실행되지 않으며, 개별 리포트는
/// RunDueReportsAsync 내부 try/catch로 격리되어 한 건 실패가 tick·타 리포트를 막지 않는다.
/// </summary>
public sealed class ReportSchedulerService : BackgroundService
{
    private readonly IVaultAiClient _vaultAi;
    private readonly VaultAiSettings _settings;
    private readonly ILogger<ReportSchedulerService> _log;
    private readonly string _reportPath;
    private readonly IReportDataProvider? _dataProvider;

    // 같은 리포트가 연속 실패하면(ReportFailureMarker.ConsecutiveFailureAlertThreshold) 일시 장애를 넘어선 신호로
    // 보고 LogCritical로 격상한다 — 표식 작성과 함께 ReportFailureMarker 가 맡는다(수동 생성 실패와 공유).

    public ReportSchedulerService(
        ILogger<ReportSchedulerService> logger,
        IVaultAiClient vaultAi,
        IOptions<VaultAiSettings> options,
        IWebHostEnvironment env,
        IReportDataProvider? dataProvider = null)
    {
        _log = logger;
        _vaultAi = vaultAi;
        _settings = options.Value;
        _dataProvider = dataProvider;
        _reportPath = Path.IsPathRooted(_settings.ReportPath)
            ? _settings.ReportPath
            : Path.Combine(env.ContentRootPath, _settings.ReportPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await RunDueReportsAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ReportScheduler 실행 오류");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One pass: every enabled report whose schedule has come due since the last tick.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can await a single pass. Driving it through
    /// <see cref="BackgroundService.StartAsync"/> instead would make the assertions depend on how
    /// far the loop happens to run before the host's start call returns — which is not a property
    /// this service promises, and not what any of those assertions are about.
    /// </remarks>
    internal async Task RunDueReportsAsync()
    {
        var now       = DateTimeOffset.Now;
        var localZone = TimeZoneInfo.Local;

        foreach (var def in LoadDefinitions())
        {
            string? outputPath = null;
            string? targetFile = null;
            try
            {
                var expression = CronExpression.Parse(def.Cron);
                var occurrence = expression.GetNextOccurrence(now.AddMinutes(-2), localZone, inclusive: true);
                if (occurrence == null || occurrence.Value > now) continue;

                var localOccurrence = TimeZoneInfo.ConvertTime(occurrence.Value, localZone).DateTime;
                targetFile = $"{localOccurrence:yyyyMMdd-HHmm}.md";
                outputPath = Path.Combine(def.FolderPath, "output", targetFile);

                if (File.Exists(outputPath))
                {
                    _log.LogDebug("리포트 이미 생성됨: {File}", outputPath);
                    continue;
                }

                _log.LogInformation("리포트 생성 시작: {Name}", def.Name);

                var reportDate = localOccurrence.Date.AddDays(-1);
                var result = await VaultAiReportsApi.RunGenerateAsync(
                    def.FolderPath, _vaultAi, _settings.ReportAgentId, _settings,
                    reportDate, targetFile, _log, _dataProvider);

                if (result.Warnings.Count > 0)
                    _log.LogWarning("리포트 생성 완료(품질 경고 포함): {File} — {Warnings}",
                        outputPath, string.Join(" | ", result.Warnings));
                else
                    _log.LogInformation("리포트 생성 완료: {File}", outputPath);
            }
            catch (ReportGenerationBusyException)
            {
                _log.LogInformation("리포트 생성 건너뜀(이미 진행 중): {Name}", def.Name);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "리포트 생성 실패: {Name}", def.Name);
                // 침묵 누락 방지 — 운영자가 목록에서 실패를 인지하도록 표식 리포트를 남긴다.
                if (outputPath is not null && targetFile is not null)
                    ReportFailureMarker.TryWrite(outputPath, def.Name, targetFile, ex, ReportFailureMarker.Origin.Scheduled, _log);
            }
        }
    }

    private List<ReportDefinition> LoadDefinitions()
    {
        var result = new List<ReportDefinition>();
        if (!Directory.Exists(_reportPath)) return result;

        foreach (var folder in Directory.GetDirectories(_reportPath).Order())
        {
            var infoPath = Path.Combine(folder, "info.json");
            if (!File.Exists(infoPath)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = doc.RootElement;

                var enabled = root.TryGetProperty("enabled", out var ep) ? ep.GetBoolean() : true;
                if (!enabled) continue;

                var name = root.TryGetProperty("name", out var np)
                    ? np.GetString() ?? Path.GetFileName(folder)
                    : Path.GetFileName(folder);
                var cron = root.GetProperty("cron").GetString()!;

                result.Add(new ReportDefinition(folder, name, cron));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "리포트 정의 로드 실패: {Folder}", folder);
            }
        }

        return result;
    }

    private record ReportDefinition(string FolderPath, string Name, string Cron);
}
