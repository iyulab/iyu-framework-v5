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

        using var response = await _httpClient
            .PostAsync("forms/libreoffice/convert", form, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Gotenberg conversion failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var result = new MemoryStream();
        await response.Content.CopyToAsync(result, ct).ConfigureAwait(false);
        result.Position = 0;
        return result;
    }
}
