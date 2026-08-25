using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iyu.DocConvert;
using Xunit;

namespace Iyu.Tests.DocConvert;

public sealed class GotenbergDocumentConverterTests
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Snapshot of the single multipart part, captured while the request is still alive
    /// (the SUT's <c>using var form = ...</c> disposes it — and disposing a <see
    /// cref="MultipartFormDataContent"/> clears its nested-content list — the instant its own
    /// <c>PostAsync</c> call returns, which is before any assertion in the test body could run).</summary>
    private sealed record CapturedPart(string? Name, string? FileName, string? ContentType, byte[] Bytes);

    private static (GotenbergDocumentConverter converter, FakeHttpMessageHandler handler, List<CapturedPart> parts,
        List<(HttpMethod method, Uri? uri)> requests) CreateConverter(
        Func<CapturedPart, Task<HttpResponseMessage>> responder)
    {
        var parts = new List<CapturedPart>();
        var requests = new List<(HttpMethod, Uri?)>();
        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            requests.Add((req.Method, req.RequestUri));
            var multipart = Assert.IsType<MultipartFormDataContent>(req.Content);
            var part = Assert.Single(multipart);
            var bytes = await part.ReadAsByteArrayAsync(ct);
            var captured = new CapturedPart(
                part.Headers.ContentDisposition?.Name,
                part.Headers.ContentDisposition?.FileName,
                part.Headers.ContentType?.MediaType,
                bytes);
            parts.Add(captured);
            return await responder(captured);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gotenberg.test/") };
        return (new GotenbergDocumentConverter(httpClient), handler, parts, requests);
    }

    private static Task<HttpResponseMessage> PdfResponse(byte[] pdfBytes) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pdfBytes),
        });

    [Fact]
    public async Task Posts_to_the_libreoffice_convert_route_with_the_files_field_and_a_mapped_extension()
    {
        var (converter, _, parts, requests) = CreateConverter(_ => PdfResponse([1, 2, 3]));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("not really a docx"));

        await converter.ConvertToPdfAsync(source, DocxContentType);

        var (method, uri) = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("http://gotenberg.test/forms/libreoffice/convert", uri!.ToString());

        var part = Assert.Single(parts);
        Assert.Equal("files", part.Name);
        Assert.Equal("document.docx", part.FileName);
        Assert.Equal(DocxContentType, part.ContentType);
    }

    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "document.docx")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "document.xlsx")]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation", "document.pptx")]
    [InlineData("application/vnd.oasis.opendocument.text", "document.odt")]
    public async Task Maps_each_supported_content_type_to_the_extension_Gotenberg_needs(
        string contentType, string expectedFileName)
    {
        var (converter, _, parts, _) = CreateConverter(_ => PdfResponse([9]));
        using var source = new MemoryStream([1, 2, 3]);

        await converter.ConvertToPdfAsync(source, contentType);

        Assert.Equal(expectedFileName, Assert.Single(parts).FileName);
    }

    [Fact]
    public async Task Throws_NotSupportedException_for_an_unmapped_content_type()
    {
        // Never reaches the HTTP call, so the responder is unused — a stub in case a defect
        // ever made it fire, which should fail loudly rather than silently returning a PDF.
        var (converter, _, _, _) = CreateConverter(_ => throw new InvalidOperationException(
            "should not have made an HTTP call for an unsupported content type"));
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => converter.ConvertToPdfAsync(source, "application/x-nonsense"));

        Assert.Contains("application/x-nonsense", ex.Message);
    }

    [Fact]
    public async Task Returns_the_converted_bytes_as_a_seekable_stream_positioned_at_zero()
    {
        byte[] pdfBytes = [0x25, 0x50, 0x44, 0x46]; // "%PDF"
        var (converter, _, _, _) = CreateConverter(_ => PdfResponse(pdfBytes));
        using var source = new MemoryStream([1, 2, 3]);

        using var result = await converter.ConvertToPdfAsync(source, DocxContentType);

        Assert.Equal(0, result.Position);
        Assert.True(result.CanSeek);
        using var buffer = new MemoryStream();
        await result.CopyToAsync(buffer);
        Assert.Equal(pdfBytes, buffer.ToArray());
    }

    [Fact]
    public async Task Does_not_dispose_the_callers_source_stream()
    {
        var (converter, _, _, _) = CreateConverter(_ => PdfResponse([1]));
        using var source = new MemoryStream([1, 2, 3]);

        await converter.ConvertToPdfAsync(source, DocxContentType);

        // Would throw ObjectDisposedException if ConvertToPdfAsync had disposed `source` —
        // see the "Buffered into a byte array" comment in GotenbergDocumentConverter.
        source.Position = 0;
        Assert.Equal(1, source.ReadByte());
    }

    [Fact]
    public async Task Throws_HttpRequestException_with_the_response_body_on_a_non_success_status()
    {
        var (converter, _, _, _) = CreateConverter(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid source file"),
        }));
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => converter.ConvertToPdfAsync(source, DocxContentType));

        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid source file", ex.Message);
    }
}
