using System.Text;
using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.AspNetCore.Http;
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
        Assert.Equal(400, statusResult.StatusCode);
        Assert.False(storage.Objects.ContainsKey("2026/07/abc"));
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
    public async Task Download_rejects_upload_token()
    {
        var storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "2026/07/abc", "application/pdf", default);

        var result = await FileGatewayHandlers.DownloadAsync(Token(FileTokenOp.Upload), Tokens, storage, Gw, default);
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
