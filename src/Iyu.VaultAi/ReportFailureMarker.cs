using Microsoft.Extensions.Logging;

namespace Iyu.VaultAi;

/// <summary>
/// The visible report a failed generation leaves in its folder's <c>output</c> — for a scheduled
/// run and for one started by hand alike — so that a missing report is never silent.
/// </summary>
/// <remarks>
/// The marker carries <see cref="VaultAiReportsApi.FailureMarker"/>, which the next run's
/// «previous report» context skips (a failure is not a trend). A run of consecutive markers
/// escalates to <c>Critical</c>.
/// </remarks>
internal static class ReportFailureMarker
{
    internal const int ConsecutiveFailureAlertThreshold = 2;

    internal enum Origin
    {
        /// <summary>The scheduler, at a slot its cron named.</summary>
        Scheduled,

        /// <summary>A generation started by hand (<c>…/new</c>).</summary>
        Manual,
    }

    /// <summary>
    /// The file a failed manual generation's marker is written to. Deliberately not a slot name
    /// (<c>yyyyMMdd-HHmm.md</c>): the scheduler skips a slot whose file exists, and a marker under
    /// that name would make it treat the minute's report as done.
    /// </summary>
    internal static string ManualFileName(DateTime when) => $"{when:yyyyMMdd-HHmmss}-manual-failed.md";

    /// <summary>Writes the marker; a failure to write it is logged, never thrown.</summary>
    internal static void TryWrite(string outputPath, string reportName, string slot, Exception ex, Origin origin,
        ILogger? log)
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
            var (title, lead) = origin == Origin.Scheduled
                ? ("리포트 자동 생성 실패", $"**{reportName}** 리포트가 예약 시각에 자동 생성되지 못했습니다. 운영자 확인이 필요합니다.")
                : ("리포트 수동 생성 실패", $"**{reportName}** 리포트의 수동 생성 요청이 실패했습니다. 요청은 받아들여졌지만(202) 리포트가 만들어지지 않았습니다.");

            var body =
                $"""
                # ⚠️ {title}

                {lead}
                {alertBanner}
                | 항목 | 값 |
                |:---|:---|
                | 대상 슬롯 | {slot} |
                | 실패 시각 | {when:yyyy-MM-dd HH:mm} |
                | 연속 실패 | {consecutive}회 |
                | 사유 | {VaultAiReportsApi.DescribeFailure(ex).Message} |

                > 이 표식은 생성 실패가 **조용히 누락**되지 않도록 시스템이 남긴 것입니다. 원인(데이터 공백·AI 응답 오류·재시도 초과 등)을 확인한 뒤 수동 재생성하거나 다음 예약 주기를 기다리세요.

                {VaultAiReportsApi.FailureMarker}
                """;

            File.WriteAllText(outputPath, body);

            if (alert)
                log?.LogCritical(
                    "리포트 연속 생성 실패 {N}회: {Name} — AI 서비스/데이터 연결 점검 필요 (마지막 사유: {Reason})",
                    consecutive, reportName, ex.Message);
            else
                log?.LogInformation("리포트 생성 실패 표식 기록: {File}", outputPath);
        }
        catch (Exception markerEx)
        {
            log?.LogWarning(markerEx, "리포트 실패 표식 기록 실패: {File}", outputPath);
        }
    }

    /// <summary>
    /// output 디렉터리에서 최신 파일부터 역순으로, 연속된 실패 표식(<see cref="VaultAiReportsApi.FailureMarker"/>)
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
}
