using System.Net;
using System.Text;
using Iyu.Tests.DocConvert;
using Iyu.UVision;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.UVision;

public sealed class UVisionEngineTests
{
    private const string Ok = """{"verdict": "OK", "image_id": "img-1"}""";

    /// <summary>One part of the multipart form, captured while the request is alive: the form is disposed as soon as the call returns.</summary>
    private sealed record Part(string? Name, string? FileName, string? ContentType, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    private sealed class Calls
    {
        public List<(HttpMethod Method, Uri? Uri, IReadOnlyList<Part> Parts)> Requests { get; } = [];
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static UVisionEngine EngineOver(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Calls? calls = null,
        long maxImageBytes = 1024,
        TimeSpan? timeout = null,
        string baseAddress = "http://engine.test/api/u-vision/")
    {
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (calls is not null)
            {
                var parts = new List<Part>();
                if (request.Content is MultipartFormDataContent form)
                {
                    foreach (var part in form)
                    {
                        parts.Add(new Part(
                            part.Headers.ContentDisposition?.Name?.Trim('"'),
                            part.Headers.ContentDisposition?.FileName?.Trim('"'),
                            part.Headers.ContentType?.MediaType,
                            await part.ReadAsByteArrayAsync(cancellationToken)));
                    }
                }

                calls.Requests.Add((request.Method, request.RequestUri, parts));
            }

            return await send(request, cancellationToken);
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        if (timeout is { } t) client.Timeout = t;
        return new UVisionEngine(client, Options.Create(new IyuUVisionOptions { BaseUrl = baseAddress, MaxImageBytes = maxImageBytes }));
    }

    private static UVisionEngine Answering(HttpStatusCode status, string body, Calls? calls = null)
        => EngineOver((_, _) => Task.FromResult(Json(status, body)), calls);

    private static Task<VisionEngineResult> Judge(UVisionEngine engine, string? deviceLabel = null, string fileName = "capture.jpg")
        => engine.InspectAsync("scenario-1", new byte[] { 1, 2, 3 }, fileName, deviceLabel);

    // ---- what is read ----

    [Fact]
    public async Task A_verdict_is_read_as_the_engine_gave_it()
    {
        var engine = Answering(HttpStatusCode.OK, """
            {"verdict": "NG", "findings": ["scratch near the left edge", "label unreadable"], "confidence": 0.82,
             "timestamp": "2026-01-01T00:00:00Z", "image_id": "img-77", "ml": {"label": "ng", "confidence": 0.9},
             "agreement": false, "requires_review": true, "posture": "review_block"}
            """);

        var result = await Judge(engine);

        Assert.True(result.Ok);
        Assert.Null(result.Failure);
        Assert.Null(result.Message);
        Assert.Equal(new VisionVerdict("NG", 0.82m, "img-77", true, "scratch near the left edge; label unreadable"), result.Verdict);
    }

    [Fact]
    public async Task A_second_opinion_the_engine_attaches_never_changes_the_verdict()
    {
        // The engine's ML check disagreeing is advice to look: the verdict stays the one the engine put in "verdict".
        var engine = Answering(HttpStatusCode.OK, """
            {"verdict": "OK", "findings": "", "confidence": 0.7, "image_id": "i", "ml": {"label": "ng", "confidence": 0.99},
             "agreement": false, "requires_review": true}
            """);

        var result = await Judge(engine);

        Assert.Equal("OK", result.Verdict!.Verdict);
        Assert.True(result.Verdict.RequiresReview);
    }

    [Fact]
    public async Task Fields_the_engine_leaves_out_are_not_reported_not_invented()
    {
        var result = await Judge(Answering(HttpStatusCode.OK, Ok));

        Assert.True(result.Ok);
        Assert.Null(result.Verdict!.Confidence);
        Assert.False(result.Verdict.RequiresReview);
        Assert.Null(result.Verdict.Findings);
    }

    // ---- what is sent ----

    [Fact]
    public async Task The_request_is_a_multipart_post_to_inspect_under_the_engines_base_path()
    {
        var calls = new Calls();
        byte[] image = [0x89, 0x50, 0x4E, 0x47, 0, 255];
        var engine = Answering(HttpStatusCode.OK, Ok, calls);

        await engine.InspectAsync("scenario-1", image, "capture.png", "station-1");

        var (method, uri, parts) = Assert.Single(calls.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("http://engine.test/api/u-vision/inspect", uri!.ToString());

        Assert.Equal(["image", "scenario_id", "device_label"], parts.Select(p => p.Name));
        var file = parts[0];
        Assert.Equal("capture.png", file.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(image, file.Bytes);
        Assert.Equal("scenario-1", parts[1].Text);
        Assert.Equal("station-1", parts[2].Text);
    }

    [Theory]
    [InlineData("http://engine.test/api/u-vision")]
    [InlineData("http://engine.test/api/u-vision/")]
    [InlineData("http://engine.test/api/u-vision//")]
    [InlineData("  http://engine.test/api/u-vision/  ")]
    public async Task The_base_path_is_kept_whether_or_not_the_address_ends_in_a_slash(string baseUrl)
    {
        var calls = new Calls();
        var options = new IyuUVisionOptions { BaseUrl = baseUrl };
        var client = new HttpClient(new FakeHttpMessageHandler((request, _) =>
        {
            calls.Requests.Add((request.Method, request.RequestUri, []));
            return Task.FromResult(Json(HttpStatusCode.OK, Ok));
        }))
        { BaseAddress = options.BaseAddress() };

        await new UVisionEngine(client, Options.Create(options)).InspectAsync("s", new byte[] { 1 }, "a.jpg", null);

        Assert.Equal("http://engine.test/api/u-vision/inspect", Assert.Single(calls.Requests).Uri!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_device_label_is_not_sent(string? label)
    {
        var calls = new Calls();

        await Judge(Answering(HttpStatusCode.OK, Ok, calls), label);

        Assert.Equal(["image", "scenario_id"], Assert.Single(calls.Requests).Parts.Select(p => p.Name));
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("A.PNG", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.JPEG", "image/jpeg")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.bmp", "image/bmp")]
    [InlineData("a.tif", "application/octet-stream")]
    [InlineData("a.dat", "application/octet-stream")]
    [InlineData("no-extension", "application/octet-stream")]
    public async Task The_content_type_follows_the_extension_and_an_unknown_one_is_not_passed_off_as_a_jpeg(string fileName, string expected)
    {
        var calls = new Calls();

        await Judge(Answering(HttpStatusCode.OK, Ok, calls), fileName: fileName);

        Assert.Equal(expected, Assert.Single(calls.Requests).Parts[0].ContentType);
    }

    [Fact]
    public async Task Only_the_file_name_is_sent_not_the_directory_it_came_from()
    {
        var calls = new Calls();

        await Judge(Answering(HttpStatusCode.OK, Ok, calls), fileName: "shots/today/part 1.jpg");

        Assert.Equal("part 1.jpg", Assert.Single(calls.Requests).Parts[0].FileName);
    }

    [Fact]
    public async Task An_image_over_the_limit_is_rejected_without_being_sent()
    {
        var calls = new Calls();
        var engine = EngineOver((_, _) => throw new InvalidOperationException("nothing should be sent"), calls, maxImageBytes: 4);

        var result = await engine.InspectAsync("scenario-1", new byte[5], "a.jpg", null);

        Assert.False(result.Ok);
        Assert.Equal(VisionEngineFailure.Rejected, result.Failure);
        Assert.Contains(nameof(IyuUVisionOptions.MaxImageBytes), result.Message);
        Assert.Empty(calls.Requests);
    }

    [Fact]
    public async Task An_image_exactly_at_the_limit_is_sent()
    {
        var calls = new Calls();
        var engine = EngineOver((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok)), calls, maxImageBytes: 4);

        var result = await engine.InspectAsync("scenario-1", new byte[4], "a.jpg", null);

        Assert.True(result.Ok);
        Assert.Single(calls.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_scenario_or_file_name_is_a_caller_error_and_nothing_is_sent(string? blank)
    {
        var calls = new Calls();
        var engine = Answering(HttpStatusCode.OK, Ok, calls);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.InspectAsync(blank!, new byte[] { 1 }, "a.jpg", null));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.InspectAsync("scenario-1", new byte[] { 1 }, blank!, null));

        Assert.Empty(calls.Requests);
    }

    // ---- when there is no verdict ----

    [Fact]
    public async Task A_503_is_unavailable_and_carries_the_engines_reason()
    {
        var result = await Judge(Answering(HttpStatusCode.ServiceUnavailable,
            """{"detection_unavailable": true, "reason": "vlm_unavailable"}"""));

        Assert.False(result.Ok);
        Assert.Null(result.Verdict);
        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
        Assert.Contains("vlm_unavailable", result.Message);
        Assert.Contains("503", result.Message);
    }

    [Fact]
    public async Task A_hint_on_a_503_is_never_read_as_a_verdict()
    {
        // The engine may attach the second opinion's label to a refusal to judge. It is not the engine's verdict.
        var result = await Judge(Answering(HttpStatusCode.ServiceUnavailable, """
            {"detection_unavailable": true, "reason": "vlm_unavailable", "ml_hint": {"label": "ng", "confidence": 0.8}}
            """));

        Assert.False(result.Ok);
        Assert.Null(result.Verdict);
        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>try later</html>")]
    [InlineData("[1]")]
    [InlineData("""{"reason": 5}""")]
    public async Task A_503_without_a_readable_reason_is_still_unavailable(string body)
    {
        var result = await Judge(Answering(HttpStatusCode.ServiceUnavailable, body));

        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
        Assert.Contains("503", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_client_error_is_a_rejection(HttpStatusCode status)
    {
        var result = await Judge(Answering(status, """{"title": "problem", "detail": "a sentence the engine wrote"}"""));

        Assert.False(result.Ok);
        Assert.Equal(VisionEngineFailure.Rejected, result.Failure);
        Assert.Contains(((int)status).ToString(), result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task A_status_that_says_not_now_or_that_the_engine_failed_is_unavailable(HttpStatusCode status)
    {
        var result = await Judge(Answering(status, "oops"));

        Assert.False(result.Ok);
        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
        Assert.Contains(((int)status).ToString(), result.Message);
    }

    [Fact]
    public async Task A_connection_failure_is_unavailable()
    {
        var engine = EngineOver((_, _) => throw new HttpRequestException("Connection refused"));

        var result = await Judge(engine);

        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
        Assert.Contains("Connection refused", result.Message);
    }

    [Fact]
    public async Task A_connection_lost_while_the_answer_is_read_is_unavailable()
    {
        var engine = EngineOver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BrokenStream()),
        }));

        var result = await Judge(engine);

        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task An_engine_that_does_not_answer_in_time_is_unavailable_not_a_cancellation()
    {
        var engine = EngineOver(
            async (_, cancellationToken) => { await Task.Delay(Timeout.Infinite, cancellationToken); return new HttpResponseMessage(); },
            timeout: TimeSpan.FromMilliseconds(100));

        var result = await Judge(engine);

        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
        Assert.Contains("time limit", result.Message);
    }

    [Fact]
    public async Task A_call_the_caller_cancels_is_cancelled_not_unavailable()
    {
        using var cancel = new CancellationTokenSource();
        var engine = EngineOver(async (_, cancellationToken) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.InspectAsync("scenario-1", new byte[] { 1 }, "a.jpg", null, cancel.Token));
    }

    [Fact]
    public async Task A_call_made_with_a_token_already_cancelled_is_cancelled()
    {
        var engine = Answering(HttpStatusCode.OK, Ok);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.InspectAsync("scenario-1", new byte[] { 1 }, "a.jpg", null, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData("""{"verdict": "MAYBE", "image_id": "i"}""")]
    [InlineData("""{"verdict": "ok", "image_id": "i"}""")]
    [InlineData("""{"image_id": "i"}""")]
    [InlineData("""{"verdict": "OK"}""")]
    [InlineData("""{"verdict": "OK", "image_id": " "}""")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("")]
    public async Task A_success_that_is_not_the_promised_shape_is_an_invalid_response_with_no_verdict(string body)
    {
        var result = await Judge(Answering(HttpStatusCode.OK, body));

        Assert.False(result.Ok);
        Assert.Null(result.Verdict);
        Assert.Equal(VisionEngineFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task A_success_with_no_content_is_an_invalid_response()
    {
        var result = await Judge(EngineOver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));

        Assert.Equal(VisionEngineFailure.InvalidResponse, result.Failure);
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
