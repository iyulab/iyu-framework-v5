using Iyu.UVision;
using Xunit;

namespace Iyu.Tests.UVision;

public class VisionResponseParserTests
{
    private static VisionVerdict Verdict(string body)
    {
        var result = VisionResponseParser.Parse(body);
        Assert.True(result.Ok, result.Message);
        return result.Verdict!;
    }

    private static string Invalid(string body)
    {
        var result = VisionResponseParser.Parse(body);
        Assert.False(result.Ok);
        Assert.Null(result.Verdict);
        Assert.Equal(VisionEngineFailure.InvalidResponse, result.Failure);
        return result.Message!;
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("NG")]
    public void Only_the_two_verdicts_are_read_and_as_they_are_written(string verdict)
    {
        Assert.Equal(verdict, Verdict($$"""{"verdict": "{{verdict}}", "image_id": "i"}""").Verdict);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("Ng")]
    [InlineData("PASS")]
    [InlineData("")]
    public void Any_other_verdict_is_not_one(string verdict)
    {
        Assert.Contains("verdict", Invalid($$"""{"verdict": "{{verdict}}", "image_id": "i"}"""));
    }

    [Theory]
    [InlineData("""{"verdict": 1, "image_id": "i"}""")]
    [InlineData("""{"verdict": null, "image_id": "i"}""")]
    [InlineData("""{"Verdict": "OK", "image_id": "i"}""")]
    [InlineData("""{"image_id": "i"}""")]
    public void A_verdict_that_is_not_a_string_under_its_own_name_is_not_one(string body)
    {
        Assert.Contains("verdict", Invalid(body));
    }

    [Theory]
    [InlineData("""{"verdict": "OK"}""")]
    [InlineData("""{"verdict": "OK", "image_id": null}""")]
    [InlineData("""{"verdict": "OK", "image_id": 7}""")]
    [InlineData("""{"verdict": "OK", "image_id": ""}""")]
    [InlineData("""{"verdict": "OK", "image_id": "  "}""")]
    public void A_verdict_without_an_image_id_is_not_trusted(string body)
    {
        Assert.Contains("image_id", Invalid(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[1]")]
    [InlineData("\"OK\"")]
    [InlineData("null")]
    public void A_body_that_is_not_a_json_object_is_invalid(string body)
    {
        Invalid(body);
    }

    [Theory]
    [InlineData("0.91", 0.91)]
    [InlineData("1", 1)]
    [InlineData("0", 0)]
    [InlineData("1e-1", 0.1)]
    public void Confidence_is_read_as_a_number(string json, double expected)
    {
        Assert.Equal((decimal)expected, Verdict($$"""{"verdict": "OK", "image_id": "i", "confidence": {{json}}}""").Confidence);
    }

    [Theory]
    [InlineData("""{"verdict": "OK", "image_id": "i"}""")]
    [InlineData("""{"verdict": "OK", "image_id": "i", "confidence": null}""")]
    public void A_confidence_the_engine_did_not_report_is_null(string body)
    {
        Assert.Null(Verdict(body).Confidence);
    }

    [Theory]
    [InlineData("\"high\"")]
    [InlineData("true")]
    [InlineData("[0.9]")]
    [InlineData("1e300")]
    public void A_confidence_that_is_not_a_number_is_invalid_not_dropped(string json)
    {
        Assert.Contains("confidence", Invalid($$"""{"verdict": "OK", "image_id": "i", "confidence": {{json}}}"""));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void The_review_advice_is_read_and_absence_is_no_advice(string json, bool expected)
    {
        Assert.Equal(expected, Verdict($$"""{"verdict": "NG", "image_id": "i", "requires_review": {{json}}}""").RequiresReview);
        Assert.False(Verdict("""{"verdict": "NG", "image_id": "i"}""").RequiresReview);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public void Review_advice_that_cannot_be_read_is_invalid_not_taken_for_no_advice(string json)
    {
        Assert.Contains("requires_review", Invalid($$"""{"verdict": "NG", "image_id": "i", "requires_review": {{json}}}"""));
    }

    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("[\"one\", \"two\"]", "one; two")]
    [InlineData("[\"one\", \"\", \"  \", null, \"two\"]", "one; two")]
    [InlineData("[\"one\", 2, true]", "one; 2; true")]
    [InlineData("[]", null)]
    [InlineData("[\"\", null]", null)]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    [InlineData("5", "5")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    public void Findings_may_be_text_or_a_list_and_an_empty_one_is_none(string json, string? expected)
    {
        Assert.Equal(expected, Verdict($$"""{"verdict": "NG", "image_id": "i", "findings": {{json}}}""").Findings);
    }

    [Fact]
    public void Findings_the_engine_left_out_are_none()
    {
        Assert.Null(Verdict("""{"verdict": "NG", "image_id": "i"}""").Findings);
    }

    [Fact]
    public void Fields_this_package_does_not_surface_are_ignored()
    {
        var verdict = Verdict("""
            {"verdict": "NG", "image_id": "i", "timestamp": "t", "posture": "review_block", "agreement": true,
             "ml": {"label": "ok", "confidence": 1}, "future_field": [1, 2, 3]}
            """);

        Assert.Equal("NG", verdict.Verdict);
    }

    [Theory]
    [InlineData("""{"detection_unavailable": true, "reason": "vlm_unavailable"}""", "vlm_unavailable")]
    [InlineData("""{"reason": "x", "ml_hint": {"label": "ng"}}""", "x")]
    [InlineData("""{"reason": ""}""", null)]
    [InlineData("""{"reason": 5}""", null)]
    [InlineData("""{"detection_unavailable": true}""", null)]
    [InlineData("[1]", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void The_reason_of_a_refusal_to_judge_is_read_when_there_is_one(string body, string? expected)
    {
        Assert.Equal(expected, VisionResponseParser.Reason(body));
    }
}
