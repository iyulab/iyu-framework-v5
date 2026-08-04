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

    // 같은 리포트가 이 횟수 이상 연속 실패하면 일시 장애를 넘어선 신호로 보고
    // LogCritical로 격상해 운영자가 인지하도록 한다(연속 실패 장기 무감지 방지).
    private const int ConsecutiveFailureAlertThreshold = 2;

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
                    TryWriteFailureMarker(outputPath, def.Name, targetFile, ex);
            }
        }
    }

    /// <summary>
    /// 생성 실패 시 해당 예약 슬롯에 가시적 표식 리포트를 기록한다.
    /// 표식에는 <see cref="VaultAiReportsApi.FailureMarker"/>가 포함되어
    /// 다음 회차의 '직전 리포트' 컨텍스트 로드에서 건너뛰어진다(연속 추세 오염 방지).
    /// </summary>
    private void TryWriteFailureMarker(string outputPath, string reportName, string targetFile, Exception ex)
    {
        try
        {
            // 같은 회차에 다른 경로로 이미 생성됐다면 덮어쓰지 않는다.
            if (File.Exists(outputPath)) return;

            var outputDir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(outputDir);

            // 이번 실패를 포함한 직전 연속 실패 횟수 — 임계 초과 시 격상 경고.
            var consecutive = CountRecentConsecutiveFailures(outputDir) + 1;
            var alert = consecutive >= ConsecutiveFailureAlertThreshold;

            var when = DateTime.Now;
            var alertBanner = alert
                ? $"\n> 🚨 **연속 {consecutive}회 실패** — 일시적 오류를 넘어선 지속 장애로 보입니다. "
                  + "AI 서비스 상태·데이터 연결·예약 설정을 즉시 점검하세요.\n"
                : string.Empty;

            var body =
                $"""
                # ⚠️ 리포트 자동 생성 실패

                **{reportName}** 리포트가 예약 시각에 자동 생성되지 못했습니다. 운영자 확인이 필요합니다.
                {alertBanner}
                | 항목 | 값 |
                |:---|:---|
                | 대상 슬롯 | {targetFile} |
                | 실패 시각 | {when:yyyy-MM-dd HH:mm} |
                | 연속 실패 | {consecutive}회 |
                | 사유 | {ex.Message} |

                > 이 표식은 생성 실패가 **조용히 누락**되지 않도록 시스템이 남긴 것입니다. 원인(데이터 공백·AI 응답 오류·재시도 초과 등)을 확인한 뒤 수동 재생성하거나 다음 예약 주기를 기다리세요.

                {VaultAiReportsApi.FailureMarker}
                """;

            File.WriteAllText(outputPath, body);

            if (alert)
                _log.LogCritical(
                    "리포트 연속 생성 실패 {N}회: {Name} — AI 서비스/데이터 연결 점검 필요 (마지막 사유: {Reason})",
                    consecutive, reportName, ex.Message);
            else
                _log.LogInformation("리포트 생성 실패 표식 기록: {File}", outputPath);
        }
        catch (Exception markerEx)
        {
            _log.LogWarning(markerEx, "리포트 실패 표식 기록 실패: {File}", outputPath);
        }
    }

    /// <summary>
    /// output 디렉터리에서 최신 슬롯부터 역순으로, 연속된 실패 표식(<see cref="VaultAiReportsApi.FailureMarker"/>)
    /// 리포트의 개수를 센다. 정상 리포트를 만나면 멈춘다(연속 실패 streak 길이).
    /// </summary>
    private static int CountRecentConsecutiveFailures(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return 0;

        var count = 0;
        foreach (var file in Directory.GetFiles(outputDir, "*.md")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            string content;
            try { content = File.ReadAllText(file); }
            catch { break; }

            if (content.Contains(VaultAiReportsApi.FailureMarker, StringComparison.Ordinal))
                count++;
            else
                break;
        }

        return count;
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
