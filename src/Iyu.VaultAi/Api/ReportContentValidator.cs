using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iyu.VaultAi;

/// <summary>
/// AI가 생성한 리포트 본문의 정제(Sanitize)·품질 검증(Validate)·구제(Salvage)를 담당.
/// RunGenerateAsync의 재시도 루프가 매 시도마다 Sanitize → Validate를 거치고,
/// 전 시도 실패 시 가장 양호한 응답을 Salvage로 강등 저장한다.
/// </summary>
internal static class ReportContentValidator
{
    // 리포트로 인정하는 최소 본문 길이 — LoadLatestOutputAsync의 컨텍스트 채택 기준과 동일
    private const int MinContentChars = 50;

    // reasoning 모드 AI의 <think>...</think> 블록
    private static readonly Regex ClosedThinkBlock =
        new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 닫는 태그 — 여는 <think> 없이 사고과정으로 시작해 </think>로만 닫는 응답 정제용
    private const string ThinkCloseTag = "</think>";

    // 정적 리포트에서 금지된 intent-json 블록 — 지시문 위반 시 제거로 교정
    private static readonly Regex IntentJsonBlock =
        new(@"```intent-json[ \t]*\r?\n[\s\S]*?```[ \t]*(\r?\n|$)", RegexOptions.Compiled);

    // view-json 블록 추출 (본문 캡처). 닫는 펜스는 라인 시작 앵커 —
    // JSON 문자열 값 안의 ```에 조기 매칭되는 오판 방지
    private static readonly Regex ViewJsonBlock =
        new(@"```view-json[ \t]*\r?\n([\s\S]*?)^[ \t]*```",
            RegexOptions.Compiled | RegexOptions.Multiline);

    // 코드 펜스 라인 — 개수가 홀수면 응답 절단 의심
    private static readonly Regex FenceLine =
        new(@"^[ ]{0,3}```", RegexOptions.Compiled | RegexOptions.Multiline);

    // 한국어 리포트에 누출된 한자(漢字)·일본어 가나(かな/カナ) — Korean-only 규칙 위반.
    // 불안정한 LLM이 "积压/排查/本周/休止/かつ" 등을 섞어 출력하는 사례가 관측됨.
    // 한글(가-힯)·라틴 약어(SPC/OCAP/Cpk)는 정상이므로 검출 대상에서 제외한다.
    // 범위: 히라가나/가타카나(぀-ヿ), CJK 확장A(㐀-䶿),
    //        CJK 통합 한자(一-鿿), CJK 호환 한자(豈-﫿).
    private static readonly Regex CjkLeak =
        new(@"[぀-ヿ㐀-䶿一-鿿豈-﫿]", RegexOptions.Compiled);

    internal sealed record ValidationResult(
        bool IsValid,
        IReadOnlyList<string> Issues,
        bool IsSalvageable)
    {
        internal static readonly ValidationResult Valid = new(true, [], false);
    }

    /// <summary>
    /// 검증 전 자동 교정: 닫힌 think 블록과 금지된 intent-json 블록 제거.
    /// 미닫힘 think(여는 &lt;think&gt;만)는 여기서 자르지 않는다 — 본문 절단 위험이 있으므로
    /// Validate가 절단 결함으로 검출해 재생성을 유도한다.
    /// </summary>
    internal static string Sanitize(string content)
    {
        content = ClosedThinkBlock.Replace(content, string.Empty);

        // vllm reasoning 파서가 여는 <think>를 소비해 content에는 사고과정 본문과
        // 닫는 </think>만 남는 경우(qwen 계열 reasoning 모델에서 관측) — 닫는 태그
        // 이전을 모두 reasoning leak으로 간주해 제거한다. 정상 리포트 본문에
        // </think>가 나타날 일은 없으므로, 마지막 닫는 태그 기준으로 절단한다.
        var lastClose = content.LastIndexOf(ThinkCloseTag, StringComparison.OrdinalIgnoreCase);
        if (lastClose >= 0)
            content = content[(lastClose + ThinkCloseTag.Length)..];

        content = IntentJsonBlock.Replace(content, string.Empty);
        return content.Trim();
    }

    /// <summary>
    /// 정제된 본문의 품질 검증.
    /// IsSalvageable=true 이슈만 있으면 Salvage로 강등 저장 가능,
    /// false 이슈(빈 본문·절단)는 재생성 외에 복구 불가.
    /// </summary>
    internal static ValidationResult Validate(string sanitized)
    {
        var issues = new List<string>();
        var salvageable = true;

        if (sanitized.Length < MinContentChars)
        {
            issues.Add($"본문이 너무 짧음 ({sanitized.Length}자 < 최소 {MinContentChars}자)");
            salvageable = false;
        }

        if (FenceLine.Matches(sanitized).Count % 2 != 0)
        {
            issues.Add("닫히지 않은 코드 펜스(```) 존재 — 응답 절단 의심");
            salvageable = false;
        }

        // 닫힌 think 블록은 Sanitize가 이미 제거 — 잔존 <think>는 미닫힘(절단) 응답
        if (sanitized.Contains("<think>", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("닫히지 않은 <think> 태그 존재 — 응답 절단 의심");
            salvageable = false;
        }

        // 한자·일본어 가나 혼입 검출 — 재생성을 유도하되 본문 자체는 구제 가능(salvageable 유지).
        // 재생성으로도 해소되지 않으면 경고와 함께 강등 저장되어 가시성은 확보된다.
        var cjk = CjkLeak.Matches(sanitized);
        if (cjk.Count > 0)
        {
            var sample = string.Concat(cjk.Select(m => m.Value).Distinct().Take(8));
            issues.Add($"한국어 리포트에 한자·일본어 문자 {cjk.Count}자 혼입(예: {sample}) — 한국어로만 작성");
        }

        var blockIndex = 0;
        foreach (Match m in ViewJsonBlock.Matches(sanitized))
        {
            blockIndex++;
            var issue = ValidateViewJsonBody(m.Groups[1].Value, blockIndex);
            if (issue is not null) issues.Add(issue);
        }

        return issues.Count == 0
            ? ValidationResult.Valid
            : new ValidationResult(false, issues, salvageable);
    }

    private static string? ValidateViewJsonBody(string body, int index)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            return $"view-json 블록 #{index}: JSON 파싱 실패 ({ex.Message})";
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
                return $"view-json 블록 #{index}: tag 누락";

            var tag = tagEl.GetString();
            if (tag != "u-chart-view" && tag != "u-declart-view")
                return $"view-json 블록 #{index}: tag가 \"u-chart-view\" 또는 \"u-declart-view\"가 아님";

            if (!root.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
                return $"view-json 블록 #{index}: properties 객체 누락";

            // declart(prose-diagram)는 properties.declaration 객체로 다이어그램을 선언한다.
            if (tag == "u-declart-view"
                && (!props.TryGetProperty("declaration", out var decl)
                    || decl.ValueKind != JsonValueKind.Object))
                return $"view-json 블록 #{index}: u-declart-view에 declaration 객체 누락";
        }

        return null;
    }

    /// <summary>
    /// 구제 가능한 본문에서 유효하지 않은 view-json 블록을 일반 json 펜스로 강등.
    /// 차트 렌더 실패 대신 원본 JSON과 경고 노트가 표시된다.
    /// </summary>
    internal static string Salvage(string sanitized)
    {
        var blockIndex = 0;
        return ViewJsonBlock.Replace(sanitized, m =>
        {
            blockIndex++;
            if (ValidateViewJsonBody(m.Groups[1].Value, blockIndex) is null)
                return m.Value;

            var sb = new StringBuilder();
            sb.AppendLine("> ⚠️ 아래 차트 정의가 유효하지 않아 원본 데이터로 표시됩니다.");
            sb.AppendLine();
            sb.Append("```json");
            sb.AppendLine();
            sb.Append(m.Groups[1].Value);
            sb.Append("```");
            return sb.ToString();
        });
    }
}
