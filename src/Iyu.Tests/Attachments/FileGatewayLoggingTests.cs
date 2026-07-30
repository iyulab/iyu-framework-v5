using System.Text;
using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iyu.Tests.Attachments;

/// <summary>Three of the gateway's rejections all answer 400, so the status code alone cannot tell an operator
/// which one fired. These pin the part that carries that information: the level (is this a caller's problem or
/// a deployment's?) and the fact that the bearer token never reaches a log sink.</summary>
public sealed class FileGatewayLoggingTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly FileAccessTokenService Tokens = new();

    private static string Token(FileTokenOp op, string? contentType = "application/pdf") => Tokens.Sign(
        new FileAccessToken(Guid.NewGuid(), "2026/07/abc", op, "order.pdf", contentType,
            DateTimeOffset.UtcNow.AddMinutes(5)), Key);

    private static DefaultHttpContext Body(string content = "data")
    {
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(content);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return ctx;
    }

    [Fact]
    public async Task A_missing_content_type_against_a_configured_allowlist_logs_a_warning()
    {
        // Warning because no caller can fix it: every upload will be refused until the minter is changed.
        var log = new RecordingLogger();
        var gw = new FileGatewayOptions { SigningKey = Key, AllowedContentTypes = ["application/pdf"] };

        await FileGatewayHandlers.UploadAsync(
            Body().Request, Token(FileTokenOp.Upload, contentType: null), Tokens, new FakeAttachmentStorage(), gw, default, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("FileAccessToken.ContentType", entry.Message);
    }

    [Fact]
    public async Task A_disallowed_content_type_logs_at_information_not_warning()
    {
        // The caller chose a type the host does not accept — expected traffic, not a deployment fault.
        var log = new RecordingLogger();
        var gw = new FileGatewayOptions { SigningKey = Key, AllowedContentTypes = ["application/pdf"] };

        await FileGatewayHandlers.UploadAsync(
            Body().Request, Token(FileTokenOp.Upload, "image/png"), Tokens, new FakeAttachmentStorage(), gw, default, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("image/png", entry.Message);
    }

    [Fact]
    public async Task An_oversized_upload_logs_both_the_declared_size_and_the_limit()
    {
        var log = new RecordingLogger();
        var gw = new FileGatewayOptions { SigningKey = Key, MaxBytes = 2 };

        await FileGatewayHandlers.UploadAsync(
            Body("far too many bytes").Request, Token(FileTokenOp.Upload), Tokens, new FakeAttachmentStorage(), gw, default, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        // Matched with their labels: a bare "2" would also be satisfied by the storage key "2026/07/abc",
        // which would make the assertion pass without the limit ever being rendered.
        Assert.Contains("declared 18 bytes", entry.Message);
        Assert.Contains("MaxBytes 2", entry.Message);
    }

    [Fact]
    public async Task An_absent_object_logs_the_key_so_orphan_sweeps_can_be_traced()
    {
        var log = new RecordingLogger();
        var gw = new FileGatewayOptions { SigningKey = Key };

        await FileGatewayHandlers.DownloadAsync(
            Token(FileTokenOp.Download), Tokens, new FakeAttachmentStorage(), gw, default, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("2026/07/abc", entry.Message);
    }

    [Fact]
    public async Task A_refusal_distinguishes_no_token_from_a_rejected_one()
    {
        var gw = new FileGatewayOptions { SigningKey = Key };

        var absent = new RecordingLogger();
        await FileGatewayHandlers.UploadAsync(Body().Request, null, Tokens, new FakeAttachmentStorage(), gw, default, absent);

        var rejected = new RecordingLogger();
        await FileGatewayHandlers.UploadAsync(
            Body().Request, Token(FileTokenOp.Download), Tokens, new FakeAttachmentStorage(), gw, default, rejected);

        Assert.Contains("no token was presented", Assert.Single(absent.Entries).Message);
        Assert.Contains("not valid for this operation", Assert.Single(rejected.Entries).Message);
    }

    [Fact]
    public async Task No_log_entry_ever_contains_the_token()
    {
        // A signed token is a bearer credential for the storage key it names. Logging one would hand a reader
        // of the logs the ability to replay the request until it expires.
        var gw = new FileGatewayOptions { SigningKey = Key, AllowedContentTypes = ["application/pdf"], MaxBytes = 2 };
        var log = new RecordingLogger();
        var storage = new FakeAttachmentStorage();

        var refused = Token(FileTokenOp.Download);
        var noType = Token(FileTokenOp.Upload, contentType: null);
        var oversized = Token(FileTokenOp.Upload);

        await FileGatewayHandlers.UploadAsync(Body().Request, refused, Tokens, storage, gw, default, log);
        await FileGatewayHandlers.UploadAsync(Body().Request, noType, Tokens, storage, gw, default, log);
        await FileGatewayHandlers.UploadAsync(Body("too many").Request, oversized, Tokens, storage, gw, default, log);
        await FileGatewayHandlers.DownloadAsync(Token(FileTokenOp.Download), Tokens, storage, gw, default, log);

        Assert.Equal(4, log.Entries.Count);
        foreach (var token in new[] { refused, noType, oversized })
            Assert.DoesNotContain(log.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_successful_transfer_logs_nothing()
    {
        // Request logging belongs to the host; the gateway only reports what a status code cannot convey.
        var log = new RecordingLogger();
        var gw = new FileGatewayOptions { SigningKey = Key };

        var result = await FileGatewayHandlers.UploadAsync(
            Body().Request, Token(FileTokenOp.Upload), Tokens, new FakeAttachmentStorage(), gw, default, log);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task The_gateway_works_without_a_logger()
    {
        // The parameter is optional so that adding it did not break existing callers; prove the null path.
        var gw = new FileGatewayOptions { SigningKey = Key, AllowedContentTypes = ["application/pdf"] };

        var result = await FileGatewayHandlers.UploadAsync(
            Body().Request, Token(FileTokenOp.Upload, contentType: null), Tokens, new FakeAttachmentStorage(), gw, default);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}
