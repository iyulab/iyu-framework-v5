using System.Text;
using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Iyu.Tests.Attachments;

public sealed class FileGatewayHandlerTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly FileAccessTokenService Tokens = new();
    private static readonly FileGatewayOptions Gw = new() { SigningKey = Key };

    private static string Token(FileTokenOp op, string storageKey = "2026/07/abc") => Tokens.Sign(
        new FileAccessToken(Guid.NewGuid(), storageKey, op, "order.pdf", "application/pdf",
            DateTimeOffset.UtcNow.AddMinutes(5)), Key);

    [Fact]
    public async Task Upload_writes_bytes_for_valid_upload_token()
    {
        var storage = new FakeAttachmentStorage();
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        ctx.Request.ContentLength = 4;

        var result = await FileGatewayHandlers.UploadAsync(ctx.Request, Token(FileTokenOp.Upload), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.True(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Upload_accepts_token_via_bearer_header()
    {
        var storage = new FakeAttachmentStorage();
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        ctx.Request.ContentLength = 4;
        ctx.Request.Headers.Authorization = "Bearer " + Token(FileTokenOp.Upload);

        var result = await FileGatewayHandlers.UploadAsync(ctx.Request, null, Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.True(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Upload_rejects_download_token()
    {
        var storage = new FakeAttachmentStorage();
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(new byte[] { 1 });

        var result = await FileGatewayHandlers.UploadAsync(ctx.Request, Token(FileTokenOp.Download), Tokens, storage, Gw, default);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task Upload_rejects_oversized_body_without_content_length()
    {
        var storage = new FakeAttachmentStorage();
        var smallGw = new FileGatewayOptions { SigningKey = Key, MaxBytes = 8 };
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("this payload is way bigger than 8 bytes"));
        // ContentLength intentionally left null — simulates chunked transfer-encoding / no header.

        var result = await FileGatewayHandlers.UploadAsync(ctx.Request, Token(FileTokenOp.Upload), Tokens, storage, smallGw, default);

        var statusResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(413, statusResult.StatusCode);
        Assert.False(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Upload_raises_server_body_limit_to_MaxBytes()
    {
        // The host default (30,000,000) sits below the gateway's 50MB default, so without alignment the
        // gap 28.6MB..MaxBytes is rejected by the server as a bare 413 and never reaches the handler.
        var storage = new FakeAttachmentStorage();
        var size = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = 30_000_000 };
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        ctx.Request.ContentLength = 4;

        var result = await FileGatewayHandlers.UploadAsync(ctx.Request, Token(FileTokenOp.Upload), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.Equal(Gw.MaxBytes, size.MaxRequestBodySize);
    }

    [Fact]
    public async Task Upload_lowers_server_body_limit_to_MaxBytes()
    {
        // Alignment goes both ways: a gateway configured below the host default must not let the server
        // stream a larger body in before LimitedStream gets to reject it.
        var storage = new FakeAttachmentStorage();
        var smallGw = new FileGatewayOptions { SigningKey = Key, MaxBytes = 1024 };
        var size = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = 30_000_000 };
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        ctx.Request.ContentLength = 4;

        await FileGatewayHandlers.UploadAsync(ctx.Request, Token(FileTokenOp.Upload), Tokens, storage, smallGw, default);

        Assert.Equal(1024, size.MaxRequestBodySize);
    }

    [Fact]
    public async Task Upload_leaves_server_body_limit_untouched_for_invalid_token()
    {
        // An unauthenticated caller must not be able to raise its own body limit.
        var storage = new FakeAttachmentStorage();
        var size = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = 30_000_000 };
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
        ctx.Request.Body = new MemoryStream(new byte[] { 1 });

        var result = await FileGatewayHandlers.UploadAsync(ctx.Request, Token(FileTokenOp.Download), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
        Assert.Equal(30_000_000, size.MaxRequestBodySize);
    }

    [Fact]
    public async Task Upload_tolerates_a_read_only_body_limit_feature()
    {
        // IIS out-of-process and any host that has already begun reading the body expose the feature as
        // read-only; assigning to it would throw, so the gateway must skip alignment and proceed.
        var storage = new FakeAttachmentStorage();
        var size = new FakeMaxRequestBodySizeFeature { IsReadOnly = true, MaxRequestBodySize = 1 };
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(size);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        ctx.Request.ContentLength = 4;

        var result = await FileGatewayHandlers.UploadAsync(ctx.Request, Token(FileTokenOp.Upload), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.Equal(1, size.MaxRequestBodySize);
    }

    /// <summary>Stand-in for the host's body-size feature. <see cref="IsReadOnly"/> models a host that has
    /// already begun reading the body (or does not permit the override), where assignment must be skipped.</summary>
    private sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; }
    }

    [Fact]
    public async Task Download_streams_for_valid_download_token()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "2026/07/abc", "application/pdf", default);

        var result = await FileGatewayHandlers.DownloadAsync(Token(FileTokenOp.Download), Tokens, storage, Gw, default);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileStreamHttpResult>(result);
    }

    [Fact]
    public async Task Download_returns_404_when_the_object_is_absent()
    {
        // Reachable in normal operation: the object is deleted while a five-minute download token is still
        // valid. This used to surface as an unhandled storage exception — a 500 that reads as an outage.
        var storage = new FakeAttachmentStorage();

        var result = await FileGatewayHandlers.DownloadAsync(Token(FileTokenOp.Download), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Download_returns_404_after_the_object_is_deleted()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "2026/07/abc", "application/pdf", default);
        await storage.DeleteAsync("2026/07/abc", default);

        var result = await FileGatewayHandlers.DownloadAsync(Token(FileTokenOp.Download), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Download_enables_range_processing()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "2026/07/abc", "application/pdf", default);

        var result = await FileGatewayHandlers.DownloadAsync(Token(FileTokenOp.Download), Tokens, storage, Gw, default);

        var file = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileStreamHttpResult>(result);
        Assert.True(file.EnableRangeProcessing);
    }

    [Fact]
    public async Task Download_rejects_upload_token()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "2026/07/abc", "application/pdf", default);

        var result = await FileGatewayHandlers.DownloadAsync(Token(FileTokenOp.Upload), Tokens, storage, Gw, default);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task Exists_returns_200_without_a_body_for_valid_download_token()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "2026/07/abc", "application/pdf", default);

        var result = await FileGatewayHandlers.ExistsAsync(Token(FileTokenOp.Download), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
    }

    [Fact]
    public async Task Exists_returns_404_when_the_object_is_absent()
    {
        var storage = new FakeAttachmentStorage();

        var result = await FileGatewayHandlers.ExistsAsync(Token(FileTokenOp.Download), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Exists_rejects_upload_token()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "2026/07/abc", "application/pdf", default);

        var result = await FileGatewayHandlers.ExistsAsync(Token(FileTokenOp.Upload), Tokens, storage, Gw, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task Delete_removes_for_valid_delete_token()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(new byte[] { 1 }), "2026/07/abc", null, default);
        var ctx = new DefaultHttpContext();

        var result = await FileGatewayHandlers.DeleteAsync(Token(FileTokenOp.Delete), ctx.Request, Tokens, storage, Gw, default);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.False(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Delete_rejects_download_token()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(new byte[] { 1 }), "2026/07/abc", null, default);
        var ctx = new DefaultHttpContext();

        var result = await FileGatewayHandlers.DeleteAsync(Token(FileTokenOp.Download), ctx.Request, Tokens, storage, Gw, default);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
        Assert.True(storage.Objects.ContainsKey("2026/07/abc"));
    }
}
