using System.Net.Http.Headers;

namespace Iyu.DocConvert;

/// <summary>
/// <see cref="IDocumentConverter"/> backed by a Gotenberg instance's LibreOffice route
/// (<c>POST {baseUrl}/forms/libreoffice/convert</c>, verified against
/// https://gotenberg.dev/docs/convert-with-libreoffice/convert-to-pdf). Gotenberg determines the
/// source format from the uploaded part's filename extension, not its declared content type, so
/// this class maps the source's MIME type to the extension Gotenberg expects and fails fast on
/// anything it does not recognize rather than guessing.
/// </summary>
public sealed class GotenbergDocumentConverter : IDocumentConverter
{
    /// <summary>
    /// MIME type → the file extension Gotenberg's LibreOffice route needs to pick the right
    /// converter. Legacy binary Office formats (.doc/.xls/.ppt) are included because LibreOffice
    /// itself reads them; this is a source-format map, not an endorsement of those formats.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExtensionByContentType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
            ["application/msword"] = ".doc",
            ["application/vnd.ms-excel"] = ".xls",
            ["application/vnd.ms-powerpoint"] = ".ppt",
            ["application/vnd.oasis.opendocument.text"] = ".odt",
            ["application/vnd.oasis.opendocument.spreadsheet"] = ".ods",
            ["application/vnd.oasis.opendocument.presentation"] = ".odp",
            ["text/csv"] = ".csv",
            ["text/plain"] = ".txt",
            ["application/rtf"] = ".rtf",
        };

    private readonly HttpClient _httpClient;

    /// <summary><paramref name="httpClient"/> is the typed client <c>AddIyuDocConvert</c>
    /// registers via <c>IHttpClientFactory</c> — its <see cref="HttpClient.BaseAddress"/> and
    /// <see cref="HttpClient.Timeout"/> already come from <see cref="GotenbergOptions"/>.</summary>
    public GotenbergDocumentConverter(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<Stream> ConvertToPdfAsync(Stream source, string sourceContentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContentType);

        if (!ExtensionByContentType.TryGetValue(sourceContentType, out var extension))
            throw new NotSupportedException(
                $"Content type '{sourceContentType}' has no known Office/OpenDocument extension " +
                $"mapping for Gotenberg's LibreOffice route. Supported: " +
                string.Join(", ", ExtensionByContentType.Keys) + ".");

        // Buffered into a byte array rather than wrapped directly in StreamContent: StreamContent
        // disposes the stream it wraps when it is itself disposed, which would dispose the
        // caller's `source` — this method's contract (see IDocumentConverter) is to read it, not
        // own it.
        using var buffered = new MemoryStream();
        await source.CopyToAsync(buffered, ct).ConfigureAwait(false);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(buffered.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(sourceContentType);
        form.Add(fileContent, "files", "document" + extension);

        // From here on every failure is the transport's or the backend's, and none of them leaves in its own
        // type: the interface says what a failure is (see IDocumentConverter), and a caller must not have to
        // know this implementation speaks HTTP to tell "unavailable" from "broken".
        try
        {
            using var response = await _httpClient
                .PostAsync("forms/libreoffice/convert", form, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var message = $"Gotenberg answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}";
                throw IsBackendUnavailable(response.StatusCode)
                    ? new DocumentConverterUnavailableException(message)
                    : new DocumentConversionFailedException(message);
            }

            var result = new MemoryStream();
            await response.Content.CopyToAsync(result, ct).ConfigureAwait(false);
            result.Position = 0;
            return result;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Not the caller's cancellation: the client's own timeout elapsed. HttpClient reports that with the
            // same exception type as a cancelled call, which is why it is told apart by whose token fired.
            throw new DocumentConverterUnavailableException(
                $"Gotenberg did not answer within the configured timeout ({_httpClient.Timeout}).", ex);
        }
        catch (HttpRequestException ex)
        {
            // Never reached, or reached and lost: the client reads the whole response before returning it, so a
            // connection that breaks part-way through the converted bytes arrives here as well.
            throw new DocumentConverterUnavailableException(
                $"Gotenberg could not be reached, or the connection was lost before it answered: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Whether a non-success status says the backend cannot serve now (a gateway that cannot reach it, overloaded, timed
    /// out — 502, 503, 504, 408, 429) rather than that it could not convert this document (any other status). A 500 is the
    /// latter: Gotenberg answers it for a document LibreOffice cannot convert, up and answering — measured, and the same
    /// bytes fail again. A starved container answers it too; that is the operator's to see in the message.
    /// </summary>
    private static bool IsBackendUnavailable(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout or System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests;
}
