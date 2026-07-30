using System.Text;
using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Attachments;

/// <summary>Covers what <see cref="FileGatewayOptions"/> actually <em>does</em>. The DI tests only assert
/// registration and the handler tests only used the defaults, so <c>AllowedContentTypes</c> and
/// <c>RoutePrefix</c> shipped with no behavioural coverage at all — which is how the allowlist came to be
/// skippable without anything turning red.</summary>
public sealed class FileGatewayOptionsBehaviourTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly FileAccessTokenService Tokens = new();

    private static string Token(string? contentType) => Tokens.Sign(
        new FileAccessToken(Guid.NewGuid(), "2026/07/abc", FileTokenOp.Upload, "order.pdf", contentType,
            DateTimeOffset.UtcNow.AddMinutes(5)), Key);

    private static (DefaultHttpContext Ctx, FakeAttachmentStorage Storage) Upload()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        ctx.Request.ContentLength = 4;
        return (ctx, new FakeAttachmentStorage());
    }

    private static FileGatewayOptions WithAllowed(params string[] types) =>
        new() { SigningKey = Key, AllowedContentTypes = types };

    [Fact]
    public async Task Allowlist_accepts_a_declared_type_that_is_listed()
    {
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token("application/pdf"), Tokens, storage, WithAllowed("application/pdf"), default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.True(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Allowlist_matching_ignores_case()
    {
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token("APPLICATION/PDF"), Tokens, storage, WithAllowed("application/pdf"), default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
    }

    [Theory]
    [InlineData("application/pdf; charset=binary")]
    [InlineData("application/pdf;charset=binary")]
    [InlineData(" application/pdf ")]
    public async Task Allowlist_ignores_media_type_parameters_and_surrounding_space(string declared)
    {
        // A media type's identity is type/subtype; parameters modify that same type. Comparing whole strings
        // refused an allowed format for a reason the caller could not see or act on.
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token(declared), Tokens, storage, WithAllowed("application/pdf"), default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.True(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Allowlist_still_matches_type_and_subtype_exactly()
    {
        // Stripping parameters must not turn into loose matching: a wildcard is not expanded, and a different
        // subtype stays refused.
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token("application/*"), Tokens, storage, WithAllowed("application/pdf"), default);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Allowlist_entries_may_themselves_carry_parameters()
    {
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token("text/csv"), Tokens, storage, WithAllowed("text/csv; charset=utf-8"), default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
    }

    [Fact]
    public async Task Allowlist_rejects_a_declared_type_that_is_not_listed()
    {
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token("image/png"), Tokens, storage, WithAllowed("application/pdf"), default);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task Allowlist_rejects_a_token_that_declares_no_type_at_all()
    {
        // The fail-open hole: the check was skipped rather than failed, so one content-type-less token
        // disabled the whole allowlist for a host that had deliberately configured one.
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token(null), Tokens, storage, WithAllowed("application/pdf"), default);

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task An_empty_allowlist_accepts_a_token_that_declares_no_type()
    {
        // Failing closed applies only to a configured allowlist — the default must stay permissive, or every
        // host that never opted into type restrictions would break.
        var (ctx, storage) = Upload();

        var result = await FileGatewayHandlers.UploadAsync(
            ctx.Request, Token(null), Tokens, storage, new FileGatewayOptions { SigningKey = Key }, default);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok>(result);
        Assert.True(storage.Objects.ContainsKey("2026/07/abc"));
    }

    [Fact]
    public async Task RoutePrefix_moves_the_endpoints_and_leaves_nothing_behind_at_the_default()
    {
        var root = Path.Combine(Path.GetTempPath(), "iyu-gw-prefix-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddIyuFileGateway(
            gw => { gw.SigningKey = Key; gw.RoutePrefix = "/attachments"; },
            (FileSystemOptions fs) => fs.RootPath = root);

        await using var app = builder.Build();
        app.MapIyuFileGateway();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        // No token: the configured route answers 401 (it exists and rejected us), the default route 404.
        var moved = await client.PutAsync("/attachments", new ByteArrayContent([1]));
        var vacated = await client.PutAsync("/files", new ByteArrayContent([1]));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, moved.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, vacated.StatusCode);

        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
