using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iyu.Uncad;

/// <summary>
/// Reads the JSON of <c>uncad summarize</c> (and, optionally, the selection output of <c>--type DIMENSION --detail</c>) into a
/// <see cref="DrawingProbe"/>.
/// </summary>
/// <remarks>
/// A field the reader does not know is ignored and a field that is missing becomes <c>null</c> (or its documented default), so a
/// newer version of the program does not break the reading. Output that is not a JSON object at all is a
/// <see cref="FormatException"/>.
/// </remarks>
internal static partial class UncadSummaryParser
{
    private const int MaxNumberCandidates = 5;

    // Labels that look like a drawing number. Data patterns, so the Korean labels ("drawing number", "drawing no.") are matched as
    // they are written in drawings. A "part name" label is deliberately not matched: in real title blocks its label and value
    // are paired unreliably with neighbouring cells such as the author and the checker.
    [GeneratedRegex(@"(DWG\.?\s*NO|DRAWING\s*NO|DRG\.?\s*NO|도면\s*번호|도번|PART\s*NO)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberLabel();

    public static DrawingProbe Parse(string summaryJson, string? dimensionDetailJson = null)
    {
        using var doc = Open(summaryJson);
        var root = doc.RootElement;

        var layers = Arr(root, "layers").Select(l => new ProbeLayer(
            Str(l, "name") ?? "", Int(l, "color_index"), Int(l, "entity_count") ?? 0)).ToList();

        var marks = dimensionDetailJson is null ? null : ReadMarks(dimensionDetailJson, layers);

        var dims = Arr(root, "dimensions").Select(d =>
        {
            var id = Long(d, "id") ?? 0;
            var (layer, color, trueColor) = marks is not null && marks.TryGetValue(id, out var m) ? m : (null, null, false);
            return new ProbeDimension(id, Str(d, "kind") ?? "UNKNOWN", Dbl(d, "measurement"), OverrideText(d), Tolerance(d), layer, color, trueColor);
        }).ToList();

        var numbers = new List<DrawingNumberCandidate>();
        foreach (var pair in Arr(root, "labelled_texts"))
        {
            var label = Plain(pair, "label");
            var value = Plain(pair, "value");
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value) || !NumberLabel().IsMatch(label)) continue;
            var candidate = new DrawingNumberCandidate(label.Trim(), value.Trim());
            if (!numbers.Contains(candidate)) numbers.Add(candidate);
            if (numbers.Count == MaxNumberCandidates) break;
        }

        return new DrawingProbe(
            root.TryGetProperty("units", out var u) ? Str(u, "name") : null,
            Int(root, "entity_count") ?? 0,
            Str(root, "confidence") ?? "UNKNOWN",
            Arr(root, "warnings").Select(w => w.GetString() ?? "").Where(w => w.Length > 0).ToList(),
            layers, dims, numbers);
    }

    private static JsonDocument Open(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw new FormatException("The uncad output is not a JSON object.");
            }

            return doc;
        }
        catch (JsonException e)
        {
            throw new FormatException("The uncad output is not JSON: " + e.Message, e);
        }
    }

    /// <summary>Dimension id to (layer, effective color index, true color). ACI 256 means "by layer", 0 means "by block".</summary>
    private static Dictionary<long, (string? Layer, int? Color, bool TrueColor)> ReadMarks(string detailJson, List<ProbeLayer> layers)
    {
        var layerColor = layers.Where(l => l.ColorIndex.HasValue).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First().ColorIndex!.Value);
        var result = new Dictionary<long, (string?, int?, bool)>();
        using var doc = Open(detailJson);
        if (!doc.RootElement.TryGetProperty("selection", out var sel)) return result;
        foreach (var e in Arr(sel, "entities"))
        {
            if (Long(e, "id") is not { } id) continue;
            var layer = e.TryGetProperty("layer", out var l) ? Str(l, "data") : null;
            var common = e.TryGetProperty("record", out var rec) && rec.TryGetProperty("common", out var c) ? c : default;
            var trueColor = common.ValueKind == JsonValueKind.Object && common.TryGetProperty("true_color", out var tc) && tc.ValueKind != JsonValueKind.Null;
            var aci = common.ValueKind == JsonValueKind.Object ? Int(common, "color_index") : null;
            int? effective = trueColor ? null
                : aci == 256 ? (layer is not null && layerColor.TryGetValue(layer, out var lc) ? lc : null)
                : aci == 0 ? null
                : aci;
            result[id] = (layer, effective, trueColor);
        }

        return result;
    }

    private static string? OverrideText(JsonElement dim)
    {
        if (!dim.TryGetProperty("text", out var t) || Str(t, "type") is null or "MEASURED") return null;
        return Str(dim, "plain") ?? Str(t, "data");
    }

    private static StatedTolerance? Tolerance(JsonElement dim)
    {
        foreach (var stack in Arr(dim, "text_stacks"))
        {
            var top = Str(stack, "top");
            var bottom = Str(stack, "bottom");
            if (string.IsNullOrWhiteSpace(top) || string.IsNullOrWhiteSpace(bottom)) continue;
            return new StatedTolerance(top.Trim(), bottom.Trim(), Dec(top), Dec(bottom), "stack");
        }

        if (dim.TryGetProperty("style_tolerance", out var st) && st.ValueKind == JsonValueKind.Object
            && st.TryGetProperty("shown", out var shown) && shown.ValueKind == JsonValueKind.True)
        {
            var up = Dbl(st, "upper");
            var lo = Dbl(st, "lower");
            if (up is not null && lo is not null)
            {
                return new StatedTolerance(
                    up.Value.ToString(CultureInfo.InvariantCulture), lo.Value.ToString(CultureInfo.InvariantCulture),
                    (decimal)up.Value, (decimal)lo.Value, "style");
            }
        }

        return null;
    }

    private static decimal? Dec(string text)
        => decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Plain(JsonElement pair, string name)
        => pair.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? (Str(p, "plain") ?? Str(p, "text") ?? "") : "";

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static double? Dbl(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
