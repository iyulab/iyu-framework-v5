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

    // ---- failures: the contract's four kinds, none of the transport's own ----

    private static GotenbergDocumentConverter ConverterOver(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null)
    {
        var client = new HttpClient(new FakeHttpMessageHandler(send)) { BaseAddress = new Uri("http://gotenberg.test/") };
        if (timeout is { } t) client.Timeout = t;
        return new GotenbergDocumentConverter(client);
    }

    /// <summary>The backend took the document and refused it: retrying the same bytes cannot help.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_document_the_backend_rejects_is_a_failed_conversion(HttpStatusCode status)
    {
        var (converter, _, _, _) = CreateConverter(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("invalid source file"),
        }));
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<DocumentConversionFailedException>(
            () => converter.ConvertToPdfAsync(source, DocxContentType));

        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.Contains("invalid source file", ex.Message);
    }

    /// <summary>The backend answered that it cannot serve now — failing, overloaded, or timed out on its side.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_backend_that_cannot_serve_now_is_unavailable(HttpStatusCode status)
    {
        var (converter, _, _, _) = CreateConverter(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("try later"),
        }));
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<DocumentConverterUnavailableException>(
            () => converter.ConvertToPdfAsync(source, DocxContentType));

        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    /// <summary>The backend is not there: the connection failure is the cause, not the exception a caller sees.</summary>
    [Fact]
    public async Task A_backend_that_cannot_be_reached_is_unavailable()
    {
        var refused = new HttpRequestException("Connection refused");
        var converter = ConverterOver((_, _) => throw refused);
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<DocumentConverterUnavailableException>(
            () => converter.ConvertToPdfAsync(source, DocxContentType));

        Assert.Same(refused, ex.InnerException);
    }

    /// <summary>
    /// The backend accepts the connection and never answers. The client's timeout elapses, which the transport
    /// reports with the same exception type as a cancelled call — here it is unavailability, not cancellation.
    /// </summary>
    [Fact]
    public async Task A_backend_that_does_not_answer_in_time_is_unavailable_not_cancelled()
    {
        var converter = ConverterOver(
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); },
            timeout: TimeSpan.FromMilliseconds(100));
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<DocumentConverterUnavailableException>(
            () => converter.ConvertToPdfAsync(source, DocxContentType));

        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    /// <summary>The caller's own cancellation stays what it is — it is not the converter being unavailable.</summary>
    [Fact]
    public async Task A_call_the_caller_cancels_is_cancelled_not_unavailable()
    {
        using var cancel = new CancellationTokenSource();
        var converter = ConverterOver(async (_, ct) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        using var source = new MemoryStream([1]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => converter.ConvertToPdfAsync(source, DocxContentType, cancel.Token));
    }

    /// <summary>The connection breaks while the converted bytes are being read.</summary>
    [Fact]
    public async Task A_connection_lost_while_the_result_is_read_is_unavailable()
    {
        var converter = ConverterOver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BrokenStream()),
        }));
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<DocumentConverterUnavailableException>(
            () => converter.ConvertToPdfAsync(source, DocxContentType));

        // The client reads the whole response before handing it over, so the broken read reaches the converter
        // as the transport's own exception — which is exactly what must not reach the caller.
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // ---- the same two failures over a real socket: no handler stands in for the transport ----

    private static GotenbergDocumentConverter ConverterAt(int port, TimeSpan timeout) =>
        new(new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = timeout });

    /// <summary>Nothing listens on the port — a sidecar that is switched off.</summary>
    [Fact]
    public async Task Over_a_real_socket_a_closed_port_is_unavailable()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();                                              // the port is known and now closed
        using var source = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<DocumentConverterUnavailableException>(
            () => ConverterAt(port, TimeSpan.FromSeconds(30)).ConvertToPdfAsync(source, DocxContentType));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    /// <summary>Something accepts the connection and never answers — a sidecar that hangs.</summary>
    [Fact]
    public async Task Over_a_real_socket_a_listener_that_never_answers_is_unavailable_after_the_timeout()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var accepting = listener.AcceptTcpClientAsync();          // accepted, then left unanswered
            using var source = new MemoryStream([1]);

            var ex = await Assert.ThrowsAsync<DocumentConverterUnavailableException>(
                () => ConverterAt(port, TimeSpan.FromMilliseconds(500)).ConvertToPdfAsync(source, DocxContentType));

            Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
            if (accepting.IsCompletedSuccessfully) (await accepting).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
