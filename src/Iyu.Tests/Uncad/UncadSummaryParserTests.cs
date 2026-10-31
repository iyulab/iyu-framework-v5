using Iyu.Uncad;
using Xunit;

namespace Iyu.Tests.Uncad;

/// <summary>
/// The reading rules for the JSON of <c>uncad summarize</c>. The inputs are synthetic documents shaped like the program's output,
/// cut down to the fields the reader looks at.
/// </summary>
public class UncadSummaryParserTests
{
    private static string Summary(
        string dimensions = "[]",
        string labelled = "[]",
        string layers = """[{"name":"0","color_index":7,"entity_count":10},{"name":"ANNOTATION","color_index":3,"entity_count":5}]""")
        => $$"""
        {
          "units": {"code": 4, "name": "mm"},
          "entity_count": 120,
          "layers": {{layers}},
          "labelled_texts": {{labelled}},
          "dimensions": {{dimensions}},
          "confidence": "HIGH",
          "warnings": ["UNHANDLEDCLASS", "VALUEOUTOFBOUNDS"]
        }
        """;

    [Fact]
    public void Reads_units_counts_confidence_and_warnings()
    {
        var probe = UncadSummaryParser.Parse(Summary());

        Assert.Equal("mm", probe.Units);
        Assert.Equal(120, probe.EntityCount);
        Assert.Equal("HIGH", probe.Confidence);
        Assert.Equal(["UNHANDLEDCLASS", "VALUEOUTOFBOUNDS"], probe.Warnings);
        Assert.Equal(2, probe.Layers.Count);
        Assert.Equal(3, probe.Layers[1].ColorIndex);
    }

    [Fact]
    public void Stacked_text_is_the_stated_tolerance()
    {
        var probe = UncadSummaryParser.Parse(Summary("""
            [{"id": 1, "kind": "DIAMETER", "measurement": 80.0, "text": {"type": "MEASURED"},
              "text_stacks": [{"top": "+0.022", "bottom": "+0.000", "separator": "^"}]}]
            """));

        var tolerance = Assert.Single(probe.Dimensions).Tolerance;
        Assert.NotNull(tolerance);
        Assert.Equal("stack", tolerance.Source);
        Assert.Equal("+0.022", tolerance.Upper);
        Assert.Equal(0.022m, tolerance.UpperValue);
        Assert.Equal(0m, tolerance.LowerValue);
    }

    [Fact]
    public void A_negative_stacked_tolerance_keeps_its_sign()
    {
        var tolerance = Assert.Single(UncadSummaryParser.Parse(Summary("""
            [{"id": 1, "kind": "ROTATED", "measurement": 97.0, "text": {"type": "MEASURED"},
              "text_stacks": [{"top": "-0.036", "bottom": "-0.058", "separator": "^"}]}]
            """)).Dimensions).Tolerance!;

        Assert.Equal(-0.036m, tolerance.UpperValue);
        Assert.Equal(-0.058m, tolerance.LowerValue);
    }

    [Fact]
    public void Stacked_text_that_is_not_a_number_keeps_its_text_and_has_no_value()
    {
        var tolerance = Assert.Single(UncadSummaryParser.Parse(Summary("""
            [{"id": 1, "kind": "ROTATED", "measurement": 10.0, "text": {"type": "MEASURED"},
              "text_stacks": [{"top": "H7", "bottom": "g6", "separator": "^"}]}]
            """)).Dimensions).Tolerance!;

        Assert.Equal(("H7", "g6"), (tolerance.Upper, tolerance.Lower));
        Assert.Null(tolerance.UpperValue);
        Assert.Null(tolerance.LowerValue);
    }

    [Fact]
    public void Tolerance_overrides_for_variable_272_are_a_format_setting_not_a_tolerance()
    {
        // Variable 272 is how many decimal places a tolerance is shown with. A dimension can carry it without stating any tolerance.
        var probe = UncadSummaryParser.Parse(Summary("""
            [{"id": 1, "kind": "ROTATED", "measurement": 100.0, "text": {"type": "MEASURED"},
              "style_tolerance": {"shown": false, "limits": false, "upper": 0.0, "lower": 0.0, "decimal_places": 2},
              "tolerance_overrides": [{"variable": 272, "value": {"type": "INTEGER", "data": 3}}]}]
            """));

        Assert.Null(Assert.Single(probe.Dimensions).Tolerance);
    }

    [Fact]
    public void A_shown_style_tolerance_is_reported_with_the_style_source()
    {
        var tolerance = Assert.Single(UncadSummaryParser.Parse(Summary("""
            [{"id": 1, "kind": "ROTATED", "measurement": 50.0, "text": {"type": "MEASURED"},
              "style_tolerance": {"shown": true, "limits": false, "upper": 0.1, "lower": 0.2, "decimal_places": 2}}]
            """)).Dimensions).Tolerance!;

        Assert.Equal("style", tolerance.Source);
        Assert.Equal(0.1m, tolerance.UpperValue);
        Assert.Equal(0.2m, tolerance.LowerValue);
    }

    [Fact]
    public void Literal_text_is_kept_and_measured_text_is_null()
    {
        var dimensions = UncadSummaryParser.Parse(Summary("""
            [{"id": 1, "kind": "DIAMETER", "measurement": 1730.0, "text": {"type": "LITERAL", "data": "BOLT CIRCLE %%C<>"}, "plain": "BOLT CIRCLE ⌀<>"},
             {"id": 2, "kind": "ROTATED", "measurement": 9.0, "text": {"type": "MEASURED"}}]
            """)).Dimensions;

        Assert.Equal("BOLT CIRCLE ⌀<>", dimensions[0].Text);
        Assert.Null(dimensions[1].Text);
        Assert.Equal(9.0, dimensions[1].Measurement);
    }

    [Fact]
    public void Drawing_number_candidates_come_from_number_labels_only_and_are_not_repeated()
    {
        // The Korean labels are matched as they are written in drawings: "drawing number" (도면번호) is one of the patterns.
        var probe = UncadSummaryParser.Parse(Summary(labelled: """
            [{"label": {"id": 1, "text": "DWG. NO.", "plain": "DWG. NO."}, "value": {"id": 2, "text": "AB-100-01", "plain": "AB-100-01"}},
             {"label": {"id": 3, "text": "DWG. NO.", "plain": "DWG. NO."}, "value": {"id": 4, "text": "AB-100-01", "plain": "AB-100-01"}},
             {"label": {"id": 5, "text": "도면번호", "plain": "도면번호"}, "value": {"id": 6, "text": "AB-100-02", "plain": "AB-100-02"}},
             {"label": {"id": 7, "text": "PREPARED", "plain": "PREPARED"}, "value": {"id": 8, "text": "SOMEONE", "plain": "SOMEONE"}},
             {"label": {"id": 9, "text": "DWG NO", "plain": "DWG NO"}, "value": {"id": 10, "text": " ", "plain": " "}}]
            """));

        Assert.Equal(
            [new DrawingNumberCandidate("DWG. NO.", "AB-100-01"), new DrawingNumberCandidate("도면번호", "AB-100-02")],
            probe.NumberCandidates);
    }

    [Fact]
    public void No_more_than_five_drawing_number_candidates_are_returned()
    {
        var pairs = string.Join(",", Enumerable.Range(1, 8).Select(i =>
            "{\"label\": {\"plain\": \"DWG NO\"}, \"value\": {\"plain\": \"AB-" + i + "\"}}"));

        var probe = UncadSummaryParser.Parse(Summary(labelled: $"[{pairs}]"));

        Assert.Equal(5, probe.NumberCandidates.Count);
        Assert.Equal("AB-5", probe.NumberCandidates[^1].Value);
    }

    [Fact]
    public void Marks_resolve_the_effective_color_for_by_layer_own_color_by_block_and_true_color()
    {
        const string detail = """
            {"selection": {"total": 4, "entities": [
              {"id": 1, "entity_type": "DIMENSION", "layer": {"type": "RESOLVED", "data": "ANNOTATION"}, "record": {"common": {"color_index": 256, "true_color": null}}},
              {"id": 2, "entity_type": "DIMENSION", "layer": {"type": "RESOLVED", "data": "0"}, "record": {"common": {"color_index": 2, "true_color": null}}},
              {"id": 3, "entity_type": "DIMENSION", "layer": {"type": "RESOLVED", "data": "0"}, "record": {"common": {"color_index": 0, "true_color": null}}},
              {"id": 4, "entity_type": "DIMENSION", "layer": {"type": "RESOLVED", "data": "0"}, "record": {"common": {"color_index": 256, "true_color": 16711680}}}
            ]}}
            """;
        var dimensions = UncadSummaryParser.Parse(Summary("""
            [{"id": 1, "kind": "ROTATED", "measurement": 1.0, "text": {"type": "MEASURED"}},
             {"id": 2, "kind": "ROTATED", "measurement": 2.0, "text": {"type": "MEASURED"}},
             {"id": 3, "kind": "ROTATED", "measurement": 3.0, "text": {"type": "MEASURED"}},
             {"id": 4, "kind": "ROTATED", "measurement": 4.0, "text": {"type": "MEASURED"}},
             {"id": 5, "kind": "ROTATED", "measurement": 5.0, "text": {"type": "MEASURED"}}]
            """), detail).Dimensions;

        Assert.Equal(("ANNOTATION", 3, false), (dimensions[0].Layer, dimensions[0].ColorIndex, dimensions[0].TrueColor)); // by layer: the layer's color
        Assert.Equal(("0", 2, false), (dimensions[1].Layer, dimensions[1].ColorIndex, dimensions[1].TrueColor));          // its own color
        Assert.Equal(("0", (int?)null, false), (dimensions[2].Layer, dimensions[2].ColorIndex, dimensions[2].TrueColor)); // by block: not knowable
        Assert.Equal(("0", (int?)null, true), (dimensions[3].Layer, dimensions[3].ColorIndex, dimensions[3].TrueColor));  // true color
        Assert.Equal((null, null, false), (dimensions[4].Layer, dimensions[4].ColorIndex, dimensions[4].TrueColor));      // not in the selection: left empty
    }

    [Fact]
    public void Without_marks_layer_and_color_stay_empty()
    {
        var dimension = Assert.Single(UncadSummaryParser.Parse(Summary(
            """[{"id": 1, "kind": "ROTATED", "measurement": 1.0, "text": {"type": "MEASURED"}}]""")).Dimensions);

        Assert.Null(dimension.Layer);
        Assert.Null(dimension.ColorIndex);
    }

    [Fact]
    public void Missing_fields_become_defaults_and_unknown_fields_are_ignored()
    {
        var probe = UncadSummaryParser.Parse("""{"future_field": {"x": 1}}""");

        Assert.Equal(0, probe.EntityCount);
        Assert.Equal("UNKNOWN", probe.Confidence);
        Assert.Null(probe.Units);
        Assert.Empty(probe.Dimensions);
        Assert.Empty(probe.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    public void Output_that_is_not_a_json_object_is_a_format_error(string json)
        => Assert.Throws<FormatException>(() => UncadSummaryParser.Parse(json));

    [Fact]
    public void A_selection_that_is_not_a_json_object_is_a_format_error()
        => Assert.Throws<FormatException>(() => UncadSummaryParser.Parse(Summary(), "not json"));
}
