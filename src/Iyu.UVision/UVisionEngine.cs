using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Iyu.UVision;

/// <summary>
/// <see cref="IVisionEngine"/> backed by U-Vision's judging route, <c>POST {BaseUrl}/inspect</c>, a multipart form of the
/// <c>image</c>, the <c>scenario_id</c> and, when given, a <c>device_label</c>. The answer is JSON in snake_case: <c>verdict</c>
/// (<c>OK</c> or <c>NG</c>), <c>confidence</c>, <c>image_id</c>, <c>requires_review</c> and <c>findings</c>.
/// </summary>
/// <remarks>
/// The engine refuses to give a verdict it cannot stand behind — when the model that judges is not available it answers
/// <c>503</c> with a body naming a reason, and keeps no record of the image. That is passed on as
/// <see cref="VisionEngineFailure.Unavailable"/>: this class never turns a missing verdict into an OK, and never reads a hint the
/// engine attaches to such an answer as a verdict.
/// <para>
/// It is a typed client: <c>AddIyuUVision</c> gives it an <see cref="HttpClient"/> whose base address and time limit come from
/// <see cref="IyuUVisionOptions"/>. Only the engine's HTTP interface is used; none of its code is referenced.
/// </para>
/// </remarks>
internal sealed class UVisionEngine : IVisionEngine
{
    private readonly HttpClient _http;
    private readonly IyuUVisionOptions _options;

    public UVisionEngine(HttpClient httpClient, IOptions<IyuUVisionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _http = httpClient;
        _options = options.Value;
    }

    public async Task<VisionEngineResult> InspectAsync(
        string scenarioId,
        ReadOnlyMemory<byte> image,
        string fileName,
        string? deviceLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        if (image.Length > _options.MaxImageBytes)
        {
            return VisionEngineResult.Fail(
                VisionEngineFailure.Rejected,
                $"The image is larger than {nameof(IyuUVisionOptions)}.{nameof(IyuUVisionOptions.MaxImageBytes)} allows; nothing was sent.");
        }

        using var form = new MultipartFormDataContent();
        var file = new ReadOnlyMemoryContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeOf(fileName));
        form.Add(file, "image", Path.GetFileName(fileName));
        form.Add(new StringContent(scenarioId), "scenario_id");
        if (!string.IsNullOrWhiteSpace(deviceLabel))
            form.Add(new StringContent(deviceLabel), "device_label");

        // The relative path is resolved against the client's base address, which ends in "/" so the engine's base path is kept.
        // From here on every failure is the transport's or the engine's, and none leaves in its own type.
        try
        {
            // The client reads the whole answer inside its time limit (the default completion option), so the limit covers the body too.
            using var response = await _http.PostAsync("inspect", form, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Interpret(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the caller's cancellation: the client's own time limit elapsed. HttpClient reports that with the same exception
            // type as a cancelled call, which is why it is told apart by whose token fired.
            return VisionEngineResult.Fail(
                VisionEngineFailure.Unavailable,
                $"The engine did not answer within the time limit ({_http.Timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} seconds).");
        }
        catch (HttpRequestException ex)
        {
            return VisionEngineResult.Fail(
                VisionEngineFailure.Unavailable,
                $"The engine could not be reached, or the connection was lost before it answered: {ex.Message}");
        }
    }

    private static VisionEngineResult Interpret(HttpStatusCode status, string body)
    {
        var code = (int)status;

        // 503 is the engine saying it cannot judge (and, deliberately, that it kept nothing). Its reason is a diagnostic; whatever
        // else the body holds — a hint from a second opinion included — is not a verdict and is not read as one.
        if (status == HttpStatusCode.ServiceUnavailable)
        {
            return VisionEngineResult.Fail(
                VisionEngineFailure.Unavailable,
                VisionResponseParser.Reason(body) is { } reason
                    ? $"The engine could not give a verdict (HTTP 503): {reason}"
                    : "The engine could not give a verdict (HTTP 503).");
        }

        // A gateway or limiter in front of the engine that asks the caller to come back later is "not now", not a refusal of this request.
        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            return VisionEngineResult.Fail(VisionEngineFailure.Unavailable, $"The engine asked to be called again later (HTTP {code}).");

        if (code is >= 400 and < 500)
            return VisionEngineResult.Fail(VisionEngineFailure.Rejected, $"The engine rejected the request (HTTP {code}).");

        if (!IsSuccess(code))
            return VisionEngineResult.Fail(VisionEngineFailure.Unavailable, $"The engine answered HTTP {code}.");

        return VisionResponseParser.Parse(body);
    }

    private static bool IsSuccess(int code) => code is >= 200 and < 300;

    /// <summary>
    /// The content type an image is sent as, by its extension. Only the type the bytes really are is claimed: what the engine
    /// accepts is the engine's to say (it answers a type it does not take with a client error, which is a rejection here),
    /// so the engine's list is not copied into this client, and an unknown extension is not passed off as a JPEG.
    /// </summary>
    internal static string ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };
}
