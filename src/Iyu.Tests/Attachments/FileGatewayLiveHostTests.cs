using System.Net.Http.Headers;
using System.Text;
using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Attachments;

/// <summary>Exercises the gateway against a <b>real Kestrel listener</b>, for the behaviour that only a real
/// server produces and that the unit tests therefore cannot reach:
/// <list type="bullet">
/// <item>body-size enforcement — neither <c>DefaultHttpContext</c> nor <c>TestServer</c> enforces a limit at
/// all, so only a live host distinguishes "the gateway's limit is in force" from "silently capped at the host
/// default of 30,000,000 bytes"</item>
/// <item>range negotiation — 206 and <c>Content-Range</c> are produced above the handler, which returns only
/// an <c>IResult</c></item>
/// </list></summary>
public sealed class FileGatewayLiveHostTests : IAsyncLifetime
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const long GatewayMaxBytes = 40L * 1024 * 1024;

    /// <summary>Above Kestrel's 30,000,000-byte default, below <see cref="GatewayMaxBytes"/> — the gap that
    /// used to surface as a bare 413 the consumer could not distinguish from the gateway's own rejection.</summary>
    private const int BodySize = 31 * 1024 * 1024;

    private WebApplication _app = default!;
    private string _root = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "iyu-gw-limit-" + Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddIyuFileGateway(
            gw => { gw.SigningKey = Key; gw.MaxBytes = GatewayMaxBytes; },
            (FileSystemOptions fs) => fs.RootPath = _root);

        _app = builder.Build();
        _app.MapIyuFileGateway();
        await _app.StartAsync();

        var address = _app.Urls.Single();
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Upload_above_the_host_default_limit_but_within_MaxBytes_succeeds()
    {
        var token = new FileAccessTokenService().Sign(
            new FileAccessToken(Guid.NewGuid(), "2026/07/big", FileTokenOp.Upload, "big.bin",
                "application/octet-stream", DateTimeOffset.UtcNow.AddMinutes(5)), Key);

        var response = await _client.PutAsync($"/files?token={token}", new ByteArrayContent(new byte[BodySize]));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BodySize, new FileInfo(Path.Combine(_root, "2026", "07", "big")).Length);
    }

    [Fact]
    public async Task Upload_beyond_MaxBytes_is_still_refused()
    {
        var smallKeyed = new FileAccessTokenService().Sign(
            new FileAccessToken(Guid.NewGuid(), "2026/07/toobig", FileTokenOp.Upload, "toobig.bin",
                "application/octet-stream", DateTimeOffset.UtcNow.AddMinutes(5)), Key);

        // A non-seekable body means no Content-Length, so the header check cannot catch this and the overrun
        // is detected mid-stream. With the server limit aligned to MaxBytes, the host's guard trips on the
        // same byte as LimitedStream and wins (it is the inner stream) — the gateway must translate that back
        // into its own structured error, or callers see a bare 413 indistinguishable from an infra rejection.
        var content = new StreamContent(new ZeroStream(GatewayMaxBytes + 1));
        var response = await _client.PutAsync($"/files?token={smallKeyed}", content);

        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("too_large", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(_root, "2026", "07", "toobig")));
    }

    [Fact]
    public async Task Download_serves_a_partial_response_for_a_Range_request()
    {
        // Range support is what lets a large download resume instead of restarting. Only a real server can
        // show it: the framework negotiates 206 and Content-Range on the response, above the handler.
        var payload = Encoding.UTF8.GetBytes("0123456789");
        await File.WriteAllBytesAsync(EnsureDir(Path.Combine(_root, "2026", "07", "ranged")), payload);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/files?token={DownloadToken("2026/07/ranged")}");
        request.Headers.Range = new RangeHeaderValue(2, 5);

        var response = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("2345", await response.Content.ReadAsStringAsync());
        Assert.Equal(payload.Length, response.Content.Headers.ContentRange!.Length);
    }

    [Fact]
    public async Task Download_advertises_range_support_and_full_length_without_a_Range_header()
    {
        var payload = Encoding.UTF8.GetBytes("0123456789");
        await File.WriteAllBytesAsync(EnsureDir(Path.Combine(_root, "2026", "07", "whole")), payload);

        var response = await _client.GetAsync($"/files?token={DownloadToken("2026/07/whole")}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(payload.Length, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Download_of_an_absent_object_is_404_not_500()
    {
        // A still-valid token outliving its object is normal operation, and the difference between 404 and
        // 500 is the difference between "gone" and "this host is broken" on an operator's dashboard.
        var response = await _client.GetAsync($"/files?token={DownloadToken("2026/07/never-stored")}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string DownloadToken(string storageKey) => new FileAccessTokenService().Sign(
        new FileAccessToken(Guid.NewGuid(), storageKey, FileTokenOp.Download, "f.bin",
            "application/octet-stream", DateTimeOffset.UtcNow.AddMinutes(5)), Key);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Reports a length without materialising the bytes, so the oversize path can be exercised
    /// without allocating 40MB+ in the test process.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
