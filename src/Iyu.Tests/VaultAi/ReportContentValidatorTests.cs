using Iyu.VaultAi;
using Xunit;

namespace Iyu.Tests.VaultAi;

public class ReportContentValidatorTests
{
    private const string ValidBody = """
        # 일일 리포트

        오늘의 주요 지표는 다음과 같습니다. 전일 대비 안정적인 흐름을 유지했습니다.

        ## 상세 내역

        - 항목 A: 100건
        - 항목 B: 200건
        """;

    private const string ValidChart = """
        ```view-json
        {
          "tag": "u-chart-view",
          "properties": {
            "type": "bar",
            "data": { "labels": ["A"], "datasets": [{ "label": "수량", "data": [1] }] }
          }
        }
        ```
        """;

    private const string ValidDeclart = """
        ```view-json
        {
          "tag": "u-declart-view",
          "properties": {
            "declaration": {
              "kind": "comparison",
              "title": "고객사 스코어카드 비교",
              "columns": [{ "label": "OTD" }],
              "rows": [{ "label": "SDC", "OTD": "68.75%" }]
            }
          }
        }
        ```
        """;

    // ── Sanitize ──────────────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_RemovesClosedThinkBlock()
    {
        var input = "<think>내부 추론 내용</think>\n" + ValidBody;
        var result = ReportContentValidator.Sanitize(input);

        Assert.DoesNotContain("<think>", result);
        Assert.Contains("# 일일 리포트", result);
    }

    [Fact]
    public void Sanitize_ThinkOnlyResponse_BecomesEmpty()
    {
        var result = ReportContentValidator.Sanitize("<think>추론만 하고 본문 없음</think>");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Sanitize_UnclosedThinkBlock_Preserved_ValidateDetectsTruncation()
    {
        // 미닫힘 think는 Sanitize가 자르지 않고(본문 절단 위험) Validate가 절단으로 검출
        var input = "<think>절단된 추론..." + "\n" + ValidBody;
        var sanitized = ReportContentValidator.Sanitize(input);
        var result    = ReportContentValidator.Validate(sanitized);

        Assert.False(result.IsValid);
        Assert.False(result.IsSalvageable);
        Assert.Contains(result.Issues, i => i.Contains("<think>"));
    }

    [Fact]
    public void Sanitize_DanglingCloseThink_NoOpeningTag_RemovesLeak()
    {
        // vllm reasoning 파서가 여는 <think>를 소비해 사고과정 본문과 닫는 </think>만
        // 남는 경우(qwen reasoning 모델) — 닫는 태그 이전 누출을 모두 제거해야 한다.
        var input = "먼저 데이터를 분석하겠습니다.\n여러 줄의 사고 과정...\n</think>\n" + ValidBody;
        var result = ReportContentValidator.Sanitize(input);

        Assert.DoesNotContain("</think>", result);
        Assert.DoesNotContain("사고 과정", result);
        Assert.StartsWith("# 일일 리포트", result);
    }

    [Fact]
    public void Sanitize_RemovesIntentJsonBlock()
    {
        var input = ValidBody + "\n```intent-json\n{ \"intent\": \"x\" }\n```\n";
        var result = ReportContentValidator.Sanitize(input);

        Assert.DoesNotContain("intent-json", result);
        Assert.Contains("# 일일 리포트", result);
    }

    [Fact]
    public void Sanitize_PreservesViewJsonBlock()
    {
        var input = ValidBody + "\n" + ValidChart;
        var result = ReportContentValidator.Sanitize(input);

        Assert.Contains("```view-json", result);
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidReport_IsValid()
    {
        var result = ReportContentValidator.Validate(ValidBody + "\n" + ValidChart);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Validate_PlainMarkdownWithoutCharts_IsValid()
    {
        var result = ReportContentValidator.Validate(ValidBody);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TooShortContent_InvalidNotSalvageable()
    {
        var result = ReportContentValidator.Validate("짧음");

        Assert.False(result.IsValid);
        Assert.False(result.IsSalvageable);
    }

    [Fact]
    public void Validate_UnclosedFence_InvalidNotSalvageable()
    {
        var input = ValidBody + "\n```view-json\n{ \"tag\": \"u-chart-view\"";
        var result = ReportContentValidator.Validate(input);

        Assert.False(result.IsValid);
        Assert.False(result.IsSalvageable);
        Assert.Contains(result.Issues, i => i.Contains("절단"));
    }

    [Fact]
    public void Validate_BrokenViewJson_InvalidButSalvageable()
    {
        var input = ValidBody + "\n```view-json\n{ broken json,, }\n```\n";
        var result = ReportContentValidator.Validate(input);

        Assert.False(result.IsValid);
        Assert.True(result.IsSalvageable);
        Assert.Contains(result.Issues, i => i.Contains("JSON 파싱 실패"));
    }

    [Fact]
    public void Validate_WrongTag_InvalidButSalvageable()
    {
        var input = ValidBody + "\n```view-json\n{ \"tag\": \"other-view\", \"properties\": {} }\n```\n";
        var result = ReportContentValidator.Validate(input);

        Assert.False(result.IsValid);
        Assert.True(result.IsSalvageable);
        Assert.Contains(result.Issues, i => i.Contains("u-chart-view"));
    }

    [Fact]
    public void Validate_ValidDeclartView_IsValid()
    {
        // declart prose-diagram 블록(u-declart-view)도 정식 view-json 태그로 인정되어야 한다.
        var result = ReportContentValidator.Validate(ValidBody + "\n" + ValidDeclart);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Validate_DeclartMissingDeclaration_InvalidButSalvageable()
    {
        // u-declart-view는 properties.declaration 객체가 필수 — 없으면 강등 대상.
        var input = ValidBody + "\n```view-json\n{ \"tag\": \"u-declart-view\", \"properties\": {} }\n```\n";
        var result = ReportContentValidator.Validate(input);

        Assert.False(result.IsValid);
        Assert.True(result.IsSalvageable);
        Assert.Contains(result.Issues, i => i.Contains("declaration"));
    }

    [Fact]
    public void Validate_BackticksInsideJsonString_NotPrematurelyMatched()
    {
        // JSON 문자열 값 안의 ```는 닫는 펜스가 아님 — 라인 앵커로 오판 방지
        var chart = """
            ```view-json
            { "tag": "u-chart-view", "properties": { "type": "bar", "data": { "labels": ["use ``` style"], "datasets": [] } } }
            ```
            """;
        var result = ReportContentValidator.Validate(ValidBody + "\n" + chart);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MissingProperties_InvalidButSalvageable()
    {
        var input = ValidBody + "\n```view-json\n{ \"tag\": \"u-chart-view\" }\n```\n";
        var result = ReportContentValidator.Validate(input);

        Assert.False(result.IsValid);
        Assert.True(result.IsSalvageable);
        Assert.Contains(result.Issues, i => i.Contains("properties"));
    }

    // ── Salvage ───────────────────────────────────────────────────────────────

    [Fact]
    public void Salvage_DemotesBrokenBlock_KeepsValidBlock()
    {
        var broken = "```view-json\n{ broken json,, }\n```";
        var input  = ValidBody + "\n" + ValidChart + "\n" + broken + "\n";

        var result = ReportContentValidator.Salvage(input);

        // 유효 블록은 view-json 그대로, 깨진 블록은 json 펜스로 강등 + 경고 노트
        Assert.Contains("```view-json", result);
        Assert.Contains("```json", result);
        Assert.Contains("유효하지 않아", result);
        Assert.Contains("broken json", result);
    }

    [Fact]
    public void Salvage_ValidContent_Unchanged()
    {
        var input  = ValidBody + "\n" + ValidChart;
        var result = ReportContentValidator.Salvage(input);

        Assert.Equal(input, result);
    }
}
