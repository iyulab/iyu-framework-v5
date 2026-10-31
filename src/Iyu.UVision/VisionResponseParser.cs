using System.Text.Json;

namespace Iyu.UVision;

/// <summary>
/// Reads the engine's answers: the JSON of a successful judgment, and the reason in the body of a refusal to judge.
/// </summary>
/// <remarks>
/// What is read is what the engine said. A verdict is exactly <c>OK</c> or <c>NG</c> — case matters — and a verdict this cannot
/// read is a failure, never a guess. An optional field is <c>null</c> (or <c>false</c> for the review advice) when the engine
/// left it out or sent <c>null</c>; when it sent a value of the wrong type that is an invalid response too, because reading
/// a review recommendation it could not make sense of as "no recommendation" would hide advice the engine gave.
/// </remarks>
internal static class VisionResponseParser
{
    public static VisionEngineResult Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Invalid("The response is not a JSON object.");

            var verdict = String(root, "verdict");
            if (verdict is not ("OK" or "NG"))
                return Invalid("The response has no \"verdict\" of exactly OK or NG.");

            var imageId = String(root, "image_id");
            if (string.IsNullOrWhiteSpace(imageId))
                return Invalid("The response has no \"image_id\".");

            decimal? confidence = null;
            if (Present(root, "confidence", out var confidenceElement))
            {
                if (confidenceElement.ValueKind != JsonValueKind.Number || !confidenceElement.TryGetDecimal(out var value))
                    return Invalid("The response's \"confidence\" is not a number.");
                confidence = value;
            }

            var requiresReview = false;
            if (Present(root, "requires_review", out var reviewElement))
            {
                if (reviewElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Invalid("The response's \"requires_review\" is not true or false.");
                requiresReview = reviewElement.GetBoolean();
            }

            return VisionEngineResult.Success(new VisionVerdict(verdict, confidence, imageId, requiresReview, Findings(root)));
        }
        catch (JsonException)
        {
            return Invalid("The response is not valid JSON.");
        }
    }

    /// <summary>
    /// The <c>reason</c> an engine gives when it refuses to judge (the body of its 503), or <c>null</c> when the body has none.
    /// </summary>
    public static string? Reason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return String(document.RootElement, "reason") is { Length: > 0 } reason ? reason : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The engine's contract says "findings" is text; a list is accepted as well and joined, so one engine that sends a list does not
    // turn a verdict into a failure. Anything else is kept as the JSON text it was (JsonElement.ToString() would write a boolean
    // as "True", which is not what the engine sent).
    private static string? Findings(JsonElement root)
    {
        if (!Present(root, "findings", out var findings))
            return null;

        var text = findings.ValueKind switch
        {
            JsonValueKind.String => findings.GetString(),
            JsonValueKind.Array => string.Join("; ", findings.EnumerateArray().Select(Text).Where(item => !string.IsNullOrWhiteSpace(item))),
            _ => findings.GetRawText(),
        };

        return string.IsNullOrEmpty(text) ? null : text;
    }

    // A list item that is null reads as nothing, so it is left out of the joined text.
    private static string? Text(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString(),
        JsonValueKind.Null => null,
        _ => item.GetRawText(),
    };

    private static bool Present(JsonElement root, string name, out JsonElement value)
        => root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static VisionEngineResult Invalid(string message)
        => VisionEngineResult.Fail(VisionEngineFailure.InvalidResponse, message);
}
