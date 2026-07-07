using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Iyu.VaultAi;

internal static class VaultAiReportsApi
{
    // folder/file 명에 path traversal 문자 허용 안 함 (영문자·숫자·하이픈·점만 허용).
    // 점만으로 구성된 세그먼트(".", "..")는 상위 디렉터리 탈출이므로 lookahead로 거부.
    private static readonly Regex SafeSegment =
        new(@"^(?!\.+$)[\w\-\.]+$", RegexOptions.Compiled);

    // fenced block 제거용 — 이전 리포트를 컨텍스트로 주입할 때 차트 JSON 오염 방지.
    // 닫는 펜스는 라인 시작 앵커 — 문자열 값 안의 ```에 조기 매칭 방지
    private static readonly Regex FencedBlock =
        new(@"```[\w-]*\r?\n[\s\S]*?^[ \t]*```", RegexOptions.Compiled | RegexOptions.Multiline);

    // 첫 H1 제목 라인(`# 제목`) — 그 아래에 작성일·분석 기준일 메타 헤더를 삽입할 위치.
    // `#` 다음 공백 1+ 로 H1만 매칭(`## …` H2는 제외).
    private static readonly Regex FirstH1Line =
        new(@"^[ \t]*#[ \t]+.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    // 제목 직후에 LLM이 스스로 만든 메타 헤더(작성일/분석 기준일 인용 + 구분선)를 제거하기 위한 패턴.
    // 시스템이 동일 헤더를 삽입하므로 중복 방지 — 선두의 빈 줄·인용 메타 라인·구분선을 연속 제거.
    private static readonly Regex LeadingMetaHeader =
        new(@"^(?:[ \t]*\r?\n|[ \t]*>[^\n]*(?:작성일|분석\s*기준일|보고\s*기준일?|생성일)[^\n]*\r?\n|[ \t]*-{3,}[ \t]*\r?\n)+",
            RegexOptions.Compiled);

    private static readonly JsonSerializerOptions IndentedJson =
        new() { WriteIndented = true };

    private const int MaxRetryCount          = 3;
    private const int RetryDelaySeconds      = 10;
    private const int PreviousContextMaxChars = 8000;

    // 리포트 폴더별 생성 로그 보관 개수 — 일간 리포트 기준 약 한 달분
    private const int MaxLogFiles = 30;

    // 이 마커 이후를 이전 리포트 로드 시 제거 → disclaimer 누적 방지
    internal const string DisclaimerMarker = "<!-- vault-ai:disclaimer -->";

    // 생성 실패 표식 리포트에 삽입 — 이전 컨텍스트 로드 시 건너뛰어 연속 추세 오염 방지
    internal const string FailureMarker = "<!-- vault-ai:failure -->";

    // 리포트 폴더별 생성 직렬화 — API 중복 요청·스케줄러 경합으로 인한 중복 AI 호출 방지
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FolderLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>해당 폴더의 생성이 현재 진행 중인지 — /new 중복 트리거 사전 필터용.
    /// 정합성의 최종 방어는 RunGenerateAsync의 gate.WaitAsync(0)이며, 이 검사는 빠른 우회로다.</summary>
    internal static bool IsGenerating(string folderPath)
        => FolderLocks.TryGetValue(Path.GetFullPath(folderPath), out var gate)
           && gate.CurrentCount == 0;

    internal record ReportInfo(string Name, string Folder, string? Icon, IReadOnlyList<string> Outputs);

    internal sealed record GenerateResult(string FileName, IReadOnlyList<string> Warnings);

    // ── R: 목록 ────────────────────────────────────────────────────────────────

    internal static IEnumerable<ReportInfo> GetReports(string reportPath)
    {
        if (!Directory.Exists(reportPath)) yield break;

        foreach (var folder in Directory.GetDirectories(reportPath).Order())
        {
            var infoPath = Path.Combine(folder, "info.json");
            if (!File.Exists(infoPath)) continue;

            string name;
            string? icon = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = doc.RootElement;
                name = root.TryGetProperty("name", out var np)
                    ? np.GetString() ?? Path.GetFileName(folder)
                    : Path.GetFileName(folder);
                icon = root.TryGetProperty("icon", out var ip) ? ip.GetString() : null;
            }
            catch { continue; }

            var outputDir = Path.Combine(folder, "output");
            IReadOnlyList<string> outputs = Directory.Exists(outputDir)
                ? Directory.GetFiles(outputDir, "*.md")
                    .Select(Path.GetFileName)
                    .Where(f => f is not null)
                    .Cast<string>()
                    .OrderByDescending(f => f)
                    .ToList()
                : [];

            yield return new ReportInfo(name, Path.GetFileName(folder), icon, outputs);
        }
    }

    // ── R: 파일 내용 ───────────────────────────────────────────────────────────

    internal static async Task GetOutputAsync(HttpContext ctx, string reportPath)
    {
        if (!TryParseSegments(ctx.Request.Path.Value, out var folder, out _, out var file)
            || file is null)
        {
            ctx.Response.StatusCode = 400; return;
        }

        var path = Path.Combine(reportPath, folder, "output", file);
        if (!File.Exists(path)) { ctx.Response.StatusCode = 404; return; }

        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.SendFileAsync(path);
    }

    // ── C: 즉시 생성 ───────────────────────────────────────────────────────────

    internal static async Task GenerateAsync(
        HttpContext ctx, string reportPath, IVaultAiClient client, Guid agentId,
        VaultAiSettings settings, IReportDataProvider? dataProvider = null)
    {
        if (!TryParseSegments(ctx.Request.Path.Value, out var folder, out _, out _))
        {
            ctx.Response.StatusCode = 400; return;
        }

        var folderPath = Path.Combine(reportPath, folder);
        if (!Directory.Exists(folderPath) || !File.Exists(Path.Combine(folderPath, "info.json")))
        {
            ctx.Response.StatusCode = 404; return;
        }

        try
        {
            var result = await RunGenerateAsync(
                folderPath, client, agentId, settings, dataProvider: dataProvider, ct: ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(
                new { file = result.FileName, folder, warnings = result.Warnings });
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // 클라이언트가 요청을 중단 — 응답 불필요
        }
        catch (ReportGenerationBusyException ex)
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }

    // ── 생성 핵심 로직 (스케줄러 + API 공용) ────────────────────────────────────

    private const string ViewJsonInstruction = """
        ## Output Rules

        You can render interactive charts inside your response using `view-json` fenced code blocks.

        **Format:**
        ```view-json
        {
          "tag": "view-tag-here",
          "properties": { "key": "value" }
        }
        ```

        **Rules:**
        1. Fence language must be `view-json` — not `json` or anything else.
        2. `tag` must be one of: `u-chart-view` (quantitative charts) or `u-declart-view` (conceptual diagrams). Never invent another tag.
        3. Output valid JSON — no comments, no trailing commas.
        4. **Never output `intent-json` blocks.** This is a static report page, not a chat interface.
        5. **Never write any footer notes** about AI authorship, data sources, or generation timestamps. The system appends these automatically after your response.
        6. **Never include meta-commentary or your own working notes** in the body — no lines like "분석 전문가 Note", "작성자:", "(이 리포트는 …로 작성)". Output only the finished report content the reader should see.
        7. **The report title must be a single H1 line (`# 제목`) at the very top, and must NOT contain any date** (e.g. write `# 전사 KPI 일일 요약 리포트`, not `# 2026-06-14 전사 KPI …`). Do NOT write your own date/metadata line under the title — the system automatically inserts a `> **작성일**: … , **분석 기준일**: …` header and a horizontal rule right below your title.
        8. **Write the entire report in Korean only — Chinese characters(漢字) and Japanese kana(かな/カナ) are strictly forbidden.** Never mix in any non-Korean script: not a single Chinese character (e.g. `本周`→write `이번 주`, `积压`→`적체`, `排查`→`점검`, `休止`→`정지`), no Japanese kana (e.g. `かつ`→`또한`), no English working notes. Only established Latin-script acronyms (SPC, OCAP, Cpk, MTTR, FPY …) may stay in their original form. Numbers and Korean only — proofread before finishing.
        9. When a table or list shows only some of the items, do NOT generalize or assert that the unseen remainder all share the same property ("모든 …건이 …이다"). Describe only what the data supports.
        10. Do NOT flag a future plan that has not yet reached its execution time (e.g. tomorrow's scheduled PM) as a defect or 🔴 just because it is "not yet done."
        11. If a metric is zero or empty across the whole target period, do NOT draw a meaningless flat chart — state "데이터 없음/미집계" instead.
        12. **Do NOT pad a section with an empty table whose rows are all "미집계/데이터 없음".** If a section has no data, write a single line ("금일 신규 없음" / "해당 데이터 없음") and move on, or omit the section entirely — never fabricate a multi-row table of placeholders.
        13. **Never invent or estimate a value that is not in the provided data or tool results.** If a per-item figure (e.g. per-customer overdue days, per-equipment downtime) is not present in the aggregated data, mark only that item "미집계" — do NOT write a guessed number, even when labeled "(추정)". Do not assign a global maximum/total to a specific named entity unless the data attributes it to that entity.

        ---

        ### u-chart-view

        Display charts using Chart.js v4. Use standard Chart.js `type`, `data`, `options` structure.

        Example:
        ```view-json
        {
          "tag": "u-chart-view",
          "properties": {
            "type": "bar",
            "data": {
              "labels": ["항목A", "항목B", "항목C"],
              "datasets": [{ "label": "수량", "data": [100, 200, 150] }]
            },
            "options": {
              "responsive": true,
              "plugins": { "title": { "display": true, "text": "차트 제목" } }
            }
          }
        }
        ```

        Supported `type`: `bar`, `line`, `pie`, `doughnut`, `radar`, `polarArea`

        ---

        ### u-declart-view

        Render **conceptual / structural diagrams** (process flows, improvement loops, yield funnels, cause-and-effect). Use this for *relationships and structure* — NOT for quantitative trends (those stay `u-chart-view`). The diagram must reflect the actual data/relationships in the report; never invent stages or causes that the data does not support.

        Put the declart declaration object under `properties.declaration`. `title` is the diagram heading. `emphasis: "primary"` highlights one key item (optional).

        ⚠️ **The `view` field belongs ONLY to `kind:"flow"` and `kind:"hierarchy"`.** `tier`, `matrix`, `comparison`, `timeline` take NO `view` field — adding `view` to them is invalid and the diagram will fail to render. Copy each schema below exactly.

        **Kinds and their exact `declaration` schema** (pick the one whose *relationship* matches; render only what the data supports):
        - `{ "kind":"flow", "view":"process", "title":…, "items":[{"label":…,"emphasis"?:"primary"}] }` — sequential steps (생산 흐름: 수주→생산→검사→출하)
        - `{ "kind":"flow", "view":"cycle", "title":…, "items":[…] }` — closed improvement loops (OCAP/PDCA: 감지→분석→조치→검증)
        - `{ "kind":"flow", "view":"funnel", "title":…, "items":[…] }` — narrowing stages, same unit only (수율: 투입→통과→합격)
        - `{ "kind":"hierarchy", "view":"fishbone", "title":효과, "nodes":[{"label":카테고리},{"label":하위원인,"parent":카테고리}] }` — root-cause (Ishikawa)
        - `{ "kind":"tier", "title":…, "items":[{"label":…,"emphasis"?:"primary"}] }` — ranked levels / priority pyramid (상위 항목이 위; 예: PAF 비용 F/A/P)
        - `{ "kind":"matrix", "title":…, "x_axis":…, "y_axis":…, "quadrants":[{"label":…,"position":"top-right|top-left|bottom-right|bottom-left","emphasis"?:"primary"}] }` — 2×2 (납기 리스크: 긴급도×금액)
        - `{ "kind":"comparison", "title":…, "columns":[{"label":기준}], "rows":[{"label":대상,"기준명":"값"}] }` — 대상×기준 스코어카드 (공급사/고객). row의 키는 column label과 일치
        - `{ "kind":"timeline", "title":…, "events":[{"date":"YYYY-MM-DD","label":…}] }` — 날짜 이벤트 ≥2건 (PM 일정)

        Examples:
        ```view-json
        {
          "tag": "u-declart-view",
          "properties": {
            "declaration": {
              "kind": "flow", "view": "cycle", "title": "OCAP 대응 사이클",
              "items": [
                { "label": "이탈 감지" },
                { "label": "원인 분석", "emphasis": "primary" },
                { "label": "조치 실행" },
                { "label": "효과 검증" }
              ]
            }
          }
        }
        ```
        ```view-json
        {
          "tag": "u-declart-view",
          "properties": {
            "declaration": {
              "kind": "hierarchy", "view": "fishbone", "title": "표면 부적합",
              "nodes": [
                { "label": "설비" }, { "label": "코터 노후", "parent": "설비" },
                { "label": "자재" }, { "label": "분말 응집", "parent": "자재" },
                { "label": "방법" }, { "label": "건조 부족", "parent": "방법" },
                { "label": "인력" }, { "label": "교육 미흡", "parent": "인력" }
              ]
            }
          }
        }
        ```

        Rules for `u-declart-view`:
        - Keep it small and legible: 3–6 process/cycle/funnel items; 3–5 fishbone categories with 1–3 sub-causes each. Do NOT pour a full data table into a diagram.
        - Use `view` ONLY with `flow`/`hierarchy`. For `tier`/`matrix`/`comparison`/`timeline` the object has just the fields shown in its schema — no `view`.
        - All labels in Korean (same CJK rule as the rest of the report).
        - If the underlying data is empty/insufficient, omit the diagram — never draw a placeholder.

        ---

        """;

    /// <param name="targetFileName">
    /// 저장할 파일명. 스케줄러는 localOccurrence 기반 이름을 전달해 dedup 보장.
    /// null이면 DateTime.Now 기반으로 결정.
    /// </param>
    internal static async Task<GenerateResult> RunGenerateAsync(
        string folderPath, IVaultAiClient client, Guid agentId,
        VaultAiSettings settings,
        DateTime? reportDate = null,
        string? targetFileName = null,
        ILogger? logger = null,
        IReportDataProvider? dataProvider = null,
        CancellationToken ct = default)
    {
        var gate = FolderLocks.GetOrAdd(
            Path.GetFullPath(folderPath), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            throw new ReportGenerationBusyException(
                $"리포트가 이미 생성 중입니다: {Path.GetFileName(folderPath)}");

        // 폴더별 실행 추적 로그 — 성공·실패·구제 모두 logs/{stem}.log 로 남겨 사후 추적을 돕는다.
        var startedAt = DateTime.Now;
        var logStem   = targetFileName is not null
            ? Path.GetFileNameWithoutExtension(targetFileName)!
            : startedAt.ToString("yyyyMMdd-HHmmss");
        var runLog = new ReportRunLog(startedAt);
        runLog.Line($"리포트 생성 시작 — 폴더: {Path.GetFileName(folderPath)}");
        runLog.Line($"보고 기준일자: {(reportDate ?? DateTime.Today.AddDays(-1)):yyyy-MM-dd}");
        if (targetFileName is not null) runLog.Line($"대상 슬롯: {targetFileName}");

        try
        {
            var result = await RunGenerateCoreAsync(
                folderPath, client, agentId, settings, reportDate, targetFileName, logger, runLog, dataProvider, ct);
            runLog.Line($"결과: 성공 — 저장 {result.FileName}"
                + (result.Warnings.Count > 0 ? $" (경고: {string.Join(" | ", result.Warnings)})" : string.Empty));
            return result;
        }
        catch (Exception ex)
        {
            runLog.Line($"결과: 실패 — {ex.GetType().Name}: {ex.Message}");
            if (ex.StackTrace is not null) runLog.Raw(ex.StackTrace);
            throw;
        }
        finally
        {
            gate.Release();
            TryWriteRunLog(folderPath, logStem, runLog);
        }
    }

    private static async Task<GenerateResult> RunGenerateCoreAsync(
        string folderPath, IVaultAiClient client, Guid agentId,
        VaultAiSettings settings,
        DateTime? reportDate, string? targetFileName,
        ILogger? logger, ReportRunLog runLog, IReportDataProvider? dataProvider, CancellationToken ct)
    {
        var promptPath = Path.Combine(folderPath, "prompt.md");
        if (!File.Exists(promptPath))
            throw new FileNotFoundException("prompt.md 없음", promptPath);

        var template      = await File.ReadAllTextAsync(promptPath, ct);
        var effectiveDate = reportDate ?? DateTime.Today.AddDays(-1);
        var userPrompt    = template.Replace("{{report_date}}", effectiveDate.ToString("yyyy-MM-dd"));

        // {{data}} 사전 집계 데이터 주입 — LLM이 임의 SQL을 생성·반복 호출하지 않도록
        // 정형 KPI를 미리 채워 넣는다(도구 호출 최소화). 출처는 소비앱의 IReportDataProvider.
        if (userPrompt.Contains("{{data}}", StringComparison.Ordinal))
        {
            string? injected = null;
            if (dataProvider is not null)
            {
                try
                {
                    injected = await dataProvider.GetReportDataAsync(folderPath, effectiveDate, ct);
                    runLog.Line($"데이터 주입: {injected?.Length ?? 0}자");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    runLog.Line($"데이터 주입 실패: {ex.GetType().Name}: {ex.Message}");
                    logger?.LogWarning(ex, "리포트 데이터 주입 실패: {Folder}", Path.GetFileName(folderPath));
                }
            }
            else
            {
                runLog.Line("데이터 주입 건너뜀: IReportDataProvider 미등록");
            }
            userPrompt = userPrompt.Replace("{{data}}", injected ?? "(사전 집계 데이터 없음 — 필요 시 도구로 직접 조회)");
        }

        // 이전 리포트 로드 — fenced block 및 disclaimer 제거 후 컨텍스트로 주입
        var previous = await LoadLatestOutputAsync(folderPath);
        runLog.Line(previous is not null
            ? $"직전 컨텍스트 로드: {previous.Value.Content.Length}자 (생성 {previous.Value.GeneratedAt:yyyy-MM-dd HH:mm})"
            : "직전 컨텍스트 없음");

        // 프롬프트 조립: 출력 규칙 → 직전 리포트 컨텍스트 → 사용자 프롬프트
        var prompt = ViewJsonInstruction
            + (previous is not null
                ? BuildPreviousContextInstruction(previous.Value.Content, previous.Value.GeneratedAt)
                : string.Empty)
            + userPrompt;
        runLog.Line($"프롬프트 조립 완료: {prompt.Length}자");

        // AI 호출 — 매 시도마다 정제·품질 검증, 실패 사유를 피드백으로 덧붙여 재생성
        string? content = null;
        var warnings = (IReadOnlyList<string>)[];
        string? salvageCandidate = null;
        IReadOnlyList<string>? salvageIssues = null;
        IReadOnlyList<string>? feedback = null;
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            runLog.Line($"시도 {attempt}/{MaxRetryCount}: AI 호출");
            try
            {
                var raw = await client.GetMessageAsync(
                    agentId, BuildAttemptPrompt(prompt, feedback), ct);
                var sanitized = ReportContentValidator.Sanitize(raw);
                var result    = ReportContentValidator.Validate(sanitized);

                runLog.Line($"  AI 응답 수신: {raw.Length}자, sanitize 후 {sanitized.Length}자");

                if (result.IsValid) { content = sanitized; runLog.Line("  검증 통과"); break; }

                runLog.Line($"  검증 실패: {string.Join(" | ", result.Issues)}");
                logger?.LogWarning(
                    "리포트 품질 검증 실패 (시도 {A}/{M}): {Issues}",
                    attempt, MaxRetryCount, string.Join(" | ", result.Issues));

                feedback = result.Issues;

                // 구제 가능한 응답 중 이슈가 가장 적은 것을 보관 — 전 시도 실패 시 강등 저장
                if (result.IsSalvageable
                    && (salvageIssues is null || result.Issues.Count < salvageIssues.Count))
                {
                    salvageCandidate = sanitized;
                    salvageIssues    = result.Issues;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 호출자가 실제로 요청을 취소(중단)한 경우만 전파 — 재시도하지 않는다.
                throw;
            }
            catch (Exception ex)
            {
                // HttpClient.Timeout으로 인한 TaskCanceledException(ct 미취소)도 여기서 재시도된다.
                lastEx = ex;
                runLog.Line($"  AI 호출 예외: {ex.GetType().Name}: {ex.Message}");
                logger?.LogWarning(ex, "AI 호출 실패 (시도 {A}/{M})", attempt, MaxRetryCount);
            }

            if (attempt < MaxRetryCount)
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), ct);
        }

        if (content is null && salvageCandidate is not null)
        {
            // 재생성으로도 차트 결함이 해소되지 않음 — 깨진 블록을 강등하고 본문은 보존
            content  = ReportContentValidator.Salvage(salvageCandidate);
            warnings = salvageIssues!;
            runLog.Line($"구제 저장: 유효하지 않은 차트 블록 강등 ({string.Join(" | ", warnings)})");
            logger?.LogWarning(
                "리포트 구제 저장: 유효하지 않은 차트 블록 강등 ({Issues})",
                string.Join(" | ", warnings));
        }

        if (content is null)
            throw new InvalidOperationException(
                $"리포트 생성 실패: {MaxRetryCount}회 시도 후에도 유효한 응답 없음"
                + (feedback is not null ? $" (마지막 검증 실패: {string.Join(" | ", feedback)})" : string.Empty),
                lastEx);

        // 제목 바로 아래에 작성일·분석 기준일 메타 헤더 삽입 — 모든 리포트 공통 형식 보장.
        // 모델이 제목에 날짜를 박거나 누락해도 시스템이 일관된 헤더를 강제한다.
        content = InsertMetaHeader(content, effectiveDate, DateTime.Now);

        // disclaimer 추가
        content += BuildDisclaimerFooter(settings.DataSourceNote, DateTime.Now);

        var outputDir = Path.Combine(folderPath, "output");
        Directory.CreateDirectory(outputDir);
        var fileName  = targetFileName ?? $"{DateTime.Now:yyyyMMdd-HHmm}.md";
        var finalPath = Path.Combine(outputDir, fileName);

        // 원자 쓰기 — 부분 파일이 목록·이전 컨텍스트 로드에 노출되지 않도록 tmp → move
        var tmpPath = finalPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmpPath, content, ct);
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* 잔존 tmp 정리 실패는 무해 */ }
            throw;
        }

        runLog.Line($"파일 저장: {fileName} ({content.Length}자)");
        return new GenerateResult(fileName, warnings);
    }

    // ── 실행 추적 로그 ────────────────────────────────────────────────────────

    /// <summary>리포트 1회 생성의 진행 상황을 시간순으로 누적하는 경량 버퍼.</summary>
    internal sealed class ReportRunLog(DateTime startedAt)
    {
        private readonly StringBuilder _sb = new();

        public void Line(string message) => _sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        public void Raw(string text) => _sb.AppendLine(text);

        public override string ToString() =>
            $"# 리포트 생성 로그\n\n시작: {startedAt:yyyy-MM-dd HH:mm:ss}\n\n{_sb}";
    }

    /// <summary>실행 로그를 logs/{stem}.log 로 기록하고 보관 한도를 초과한 오래된 로그를 정리한다.</summary>
    private static void TryWriteRunLog(string folderPath, string logStem, ReportRunLog runLog)
    {
        try
        {
            var logDir = Path.Combine(folderPath, "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, logStem + ".log"), runLog.ToString());
            PruneLogs(logDir, MaxLogFiles);
        }
        catch { /* 로그 기록 실패는 리포트 생성 자체에 영향을 주지 않는다 */ }
    }

    /// <summary>logs 디렉터리에서 파일명(=슬롯 시각) 최신순 상위 keep개만 남기고 삭제.</summary>
    internal static void PruneLogs(string logDir, int keep)
    {
        var files = Directory.GetFiles(logDir, "*.log")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

        foreach (var old in files.Skip(keep))
            try { File.Delete(old); } catch { /* 정리 실패는 무해 */ }
    }

    // ── C: 수동 생성 트리거 (GET .../{folder}/new) ─────────────────────────────
    // 예약 생성이 누락된 슬롯을 URL 직접 호출로 즉시 재생성. 생성은 백그라운드로 던지고
    // 즉시 202를 응답한다(긴 AI 호출을 브라우저가 기다리지 않도록).
    // GET이 부수효과를 갖지만, 브라우저·외부 모니터링이 손쉽게 트리거하도록 의도한 편의 경로.

    internal static Task GenerateNewAsync(
        HttpContext ctx, string reportPath,
        IVaultAiClient client, Guid agentId, VaultAiSettings settings, string folder,
        IReportDataProvider? dataProvider = null)
    {
        if (!SafeSegment.IsMatch(folder))
        {
            ctx.Response.StatusCode = 400;
            return ctx.Response.WriteAsJsonAsync(new { error = "잘못된 리포트 경로입니다." });
        }

        var folderPath = Path.Combine(reportPath, folder);
        if (!Directory.Exists(folderPath) || !File.Exists(Path.Combine(folderPath, "info.json")))
        {
            ctx.Response.StatusCode = 404;
            return ctx.Response.WriteAsJsonAsync(new { error = "리포트를 찾을 수 없습니다." });
        }

        // 이미 진행 중이면 새 작업을 띄우지 않고 무시 — 여러 번 호출돼도 중복 생성 안 됨.
        if (IsGenerating(folderPath))
        {
            ctx.Response.StatusCode = 200; // 정상 — 단지 새 작업을 시작하지 않았을 뿐
            return ctx.Response.WriteAsJsonAsync(new
            {
                message = "이미 리포트 생성이 진행 중입니다. 잠시 후 리포트를 확인하세요."
            });
        }

        // 요청 수명과 분리해 백그라운드로 생성(ctx.RequestAborted 사용 금지 — 응답 직후 취소됨).
        // 사전 검사와 시작 사이의 경합은 RunGenerateAsync의 gate가 최종적으로 흡수한다.
        _ = Task.Run(() => RunGenerateInBackgroundAsync(folderPath, client, agentId, settings, dataProvider));

        ctx.Response.StatusCode = 202; // Accepted — 요청 수락, 생성은 비동기로 진행
        return ctx.Response.WriteAsJsonAsync(new
        {
            message = "리포트 생성이 시작되었습니다. 잠시 후 리포트를 확인하세요."
        });
    }

    private static async Task RunGenerateInBackgroundAsync(
        string folderPath, IVaultAiClient client, Guid agentId, VaultAiSettings settings,
        IReportDataProvider? dataProvider)
    {
        try
        {
            await RunGenerateAsync(folderPath, client, agentId, settings,
                dataProvider: dataProvider, ct: CancellationToken.None);
        }
        catch (ReportGenerationBusyException)
        {
            // 이미 생성이 진행 중 — 트리거 무시
        }
        catch
        {
            // 백그라운드 실패는 폴더별 실행 로그(ReportRunLog)에 이미 기록됨
        }
    }

    private static string BuildAttemptPrompt(string prompt, IReadOnlyList<string>? feedback)
    {
        if (feedback is null) return prompt;

        return prompt + $"""


            ## 재생성 지시

            직전 생성 시도의 응답에서 아래 문제가 발견되어 재생성합니다. 동일한 문제가 재발하지 않도록 작성하세요:
            {string.Join("\n", feedback.Select(i => $"- {i}"))}
            """;
    }

    // ── U: info.json + prompt.md 수정 ─────────────────────────────────────────

    internal static async Task PatchInfoAsync(HttpContext ctx, string reportPath)
    {
        if (!TryParseSegments(ctx.Request.Path.Value, out var folder, out _, out _))
        {
            ctx.Response.StatusCode = 400; return;
        }

        var folderPath = Path.Combine(reportPath, folder);
        var infoPath   = Path.Combine(folderPath, "info.json");
        var promptPath = Path.Combine(folderPath, "prompt.md");

        if (!File.Exists(infoPath)) { ctx.Response.StatusCode = 404; return; }

        JsonNode patch;
        try
        {
            patch = (await JsonNode.ParseAsync(ctx.Request.Body))!;
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "요청 본문 JSON 파싱 실패" });
            return;
        }

        // 현재 info.json 읽기 → 패치 적용 → 저장
        var current = JsonNode.Parse(await File.ReadAllTextAsync(infoPath))!.AsObject();
        foreach (var kv in patch.AsObject())
        {
            if (kv.Key == "prompt") continue; // 별도 처리
            current[kv.Key] = kv.Value?.DeepClone();
        }
        await File.WriteAllTextAsync(infoPath, current.ToJsonString(IndentedJson));

        // prompt 필드가 있으면 prompt.md 저장
        if (patch["prompt"] is JsonValue promptNode)
            await File.WriteAllTextAsync(promptPath, promptNode.GetValue<string>());

        ctx.Response.StatusCode = 200;
        await ctx.Response.WriteAsJsonAsync(
            GetReports(reportPath).FirstOrDefault(r => r.Folder == folder));
    }

    // ── D: 출력 파일 삭제 ──────────────────────────────────────────────────────

    internal static Task DeleteOutputAsync(HttpContext ctx, string reportPath)
    {
        if (!TryParseSegments(ctx.Request.Path.Value, out var folder, out _, out var file)
            || file is null)
        {
            ctx.Response.StatusCode = 400; return Task.CompletedTask;
        }

        var path = Path.Combine(reportPath, folder, "output", file);
        if (!File.Exists(path)) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }

        File.Delete(path);
        ctx.Response.StatusCode = 204;
        return Task.CompletedTask;
    }

    // ── 이전 리포트 로드 및 정제 ──────────────────────────────────────────────

    private static async Task<(string Content, DateTime? GeneratedAt)?> LoadLatestOutputAsync(string folderPath)
    {
        var outputDir = Path.Combine(folderPath, "output");
        if (!Directory.Exists(outputDir)) return null;

        // 최신순으로 순회 — 비어 있거나 깨진 파일은 건너뛰고 차선 파일로 폴백
        foreach (var file in Directory.GetFiles(outputDir, "*.md")
                     .Select(Path.GetFileName)
                     .Where(f => f is not null)
                     .Cast<string>()
                     .OrderByDescending(f => f))
        {
            var raw = await File.ReadAllTextAsync(Path.Combine(outputDir, file));

            // 생성 실패 표식은 컨텍스트로 쓰지 않음 — 직전 '정상' 리포트로 폴백
            if (raw.Contains(FailureMarker, StringComparison.Ordinal)) continue;

            // disclaimer 마커 이후 제거 — 누적 오염 방지
            var markerIdx = raw.IndexOf(DisclaimerMarker, StringComparison.Ordinal);
            if (markerIdx >= 0) raw = raw[..markerIdx].TrimEnd();

            // fenced block 제거 — 차트 JSON/코드가 다음 리포트에 그대로 복사되는 오염 방지
            raw = FencedBlock.Replace(raw, "[차트/코드 생략]");

            if (raw.Length <= 50) continue;

            // 절단 시 머리·꼬리 모두 보존 — 미결/Follow-up 섹션은 관례상 말미에 위치
            if (raw.Length > PreviousContextMaxChars)
            {
                var tailLen = PreviousContextMaxChars / 4;
                var headLen = PreviousContextMaxChars - tailLen;
                raw = raw[..headLen] + "\n\n*(중략)*\n\n" + raw[^tailLen..];
            }

            return (raw, ParseOutputTimestamp(file));
        }

        return null;
    }

    /// <summary>출력 파일명 "yyyyMMdd-HHmm.md"에서 생성 시각 추출. 형식 불일치 시 null.</summary>
    private static DateTime? ParseOutputTimestamp(string fileName) =>
        DateTime.TryParseExact(
            Path.GetFileNameWithoutExtension(fileName), "yyyyMMdd-HHmm",
            null, System.Globalization.DateTimeStyles.None, out var dt)
            ? dt : null;

    private static string BuildPreviousContextInstruction(string previousContent, DateTime? generatedAt)
    {
        var generatedNote = generatedAt is not null
            ? $" (생성 시각: {generatedAt:yyyy-MM-dd HH:mm})"
            : string.Empty;

        return $"""
        ## 직전 리포트 컨텍스트

        아래는 직전에 생성된 같은 종류의 리포트입니다{generatedNote}. 일간 리포트라면 통상 전일분이지만, 주간 리포트이거나 같은 날 재생성된 경우일 수 있으니 생성 시각을 기준으로 비교 기간을 판단하세요. 현재 리포트 작성 시 다음을 반영하세요:
        - 요약 섹션에 직전 리포트 대비 주요 변동을 포함하세요.
        - 수치 변화는 ▲(증가)/▼(감소)와 변화량으로 명시하세요 (예: **42건** ▲3). 변화가 없으면 "변동 없음"으로만 표기하고 ▲0/▼0 처럼 방향 화살표에 0을 붙이지 마세요.
        - **직전 리포트의 구체 수치(측정값·OCAP 건수·재고량·금액 등)를 현재 회차의 값으로 재인용·재서술하지 마세요.** 현재 수치는 오직 이번 회차의 [집계 데이터] 또는 도구 조회 결과만 사용합니다. 직전 수치는 "직전 N → 현재 M" 비교 맥락에서만 인용하고, 현재 데이터가 없으면 직전 값을 현재처럼 단정하지 말고 "현재 미집계"로 표기하세요.
        - 현재 데이터가 비어 직전과 같은 스냅샷을 재사용하는 경우, 가짜 "직전 대비" 비교 수치를 만들지 말고 "데이터 공백 — 직전 대비 비교 불가"로 명시하세요.
        - 직전 리포트의 "N일 연속" 같은 추세 서술은, 이번 회차 데이터로 재확인될 때만 이어가세요. 직전 텍스트만 근거로 "연속/지속"을 단정하지 마세요.
        - 직전 리포트의 미결 사항이 있으면 현황을 업데이트하세요.
        - 리포트 말미에 "## 연속 추세 평가" 섹션을 반드시 포함하세요. 직전 리포트에 동일 섹션이 있으면 그 내용을 이어받아 갱신하고(개선/악화/유지 흐름 유지), 없으면 새로 시작하세요. 이 섹션은 여러 날에 걸친 흐름(연속 악화, 반복 발생, 개선 정착 여부)을 누적 평가하는 자리입니다.
        - 직전 내용을 그대로 복사하지 마세요 — 비교·분석 목적으로만 활용하세요.

        <previous_report>
        {previousContent}
        </previous_report>

        ---

        """;
    }

    /// <summary>
    /// 리포트 제목(첫 H1) 바로 아래에 작성일·분석 기준일 메타 헤더와 구분선을 삽입한다.
    /// 제목 형식을 모든 리포트에 일관되게 강제 — 제목에 날짜가 박히거나 메타가 누락돼도
    /// 시스템이 표준 헤더를 보장한다. H1 제목이 없으면 본문 맨 위에 헤더만 올린다.
    /// </summary>
    private static string InsertMetaHeader(string content, DateTime reportDate, DateTime generatedAt)
    {
        var header = $"\n\n> **작성일**: {generatedAt:yyyy-MM-dd}, **분석 기준일**: {reportDate:yyyy-MM-dd}\n\n---";

        var m = FirstH1Line.Match(content);
        if (m.Success)
        {
            var insertAt = m.Index + m.Length;
            // 제목 직후 LLM이 만든 중복 메타 헤더를 걷어내고(멱등), 시스템 헤더를 삽입한다.
            var rest = LeadingMetaHeader.Replace(content[insertAt..], string.Empty);
            return content[..insertAt] + header + "\n\n" + rest.TrimStart('\r', '\n', ' ', '\t');
        }

        return header.TrimStart() + "\n\n" + content;
    }

    private static string BuildDisclaimerFooter(string? dataSourceNote, DateTime generatedAt)
    {
        var sourceNote = dataSourceNote is not null ? $" | 데이터 출처: {dataSourceNote}" : string.Empty;
        return $"""


            {DisclaimerMarker}

            ---

            > **[AI 자동 생성 리포트]** 본 리포트는 AI(LLM)에 의해 자동 생성되었습니다. AI 특성상 부정확한 내용(할루시네이션)이 포함될 수 있으므로, 중요한 의사결정 시 원본 데이터를 반드시 확인하시기 바랍니다.
            > 생성 일시: {generatedAt:yyyy-MM-dd HH:mm}{sourceNote}
            """;
    }

    // ── 경로 파싱 헬퍼 ────────────────────────────────────────────────────────
    // "/api/vault-ai/reports/{folder}[/{action}[/{file}]]"
    // 예) /reports/01-foo/generate     → folder=01-foo, action=generate, file=null
    //     /reports/01-foo/outputs/x.md → folder=01-foo, action=outputs,  file=x.md
    //     /reports/01-foo              → folder=01-foo, action=null,      file=null

    internal static bool TryParseSegments(
        string? reqPath,
        out string folder,
        out string? action,
        out string? file)
    {
        folder = action = file = null!;
        const string prefix = "/api/vault-ai/reports/";
        if (reqPath is null || !reqPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = reqPath[prefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        folder = parts[0];
        action = parts.Length > 1 ? parts[1] : null;
        file   = parts.Length > 2 ? parts[2] : null;

        return SafeSegment.IsMatch(folder)
            && (file is null || SafeSegment.IsMatch(file));
    }
}

/// <summary>같은 리포트 폴더에 대한 생성이 이미 진행 중일 때 발생. API는 409로 매핑.</summary>
internal sealed class ReportGenerationBusyException(string message) : Exception(message);
