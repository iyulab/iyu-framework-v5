using Iyu.Core.Attachments;
using Microsoft.AspNetCore.Http;

namespace Iyu.FileServer;

/// <summary>Static gateway handlers (separated from routing for unit testability). Trust the signed token only — no DB.</summary>
public static class FileGatewayHandlers
{
    public static async Task<IResult> UploadAsync(
        HttpRequest request, string? token, FileAccessTokenService tokens,
        IAttachmentStorage storage, FileGatewayOptions gw, CancellationToken ct)
    {
        var raw = token ?? BearerToken(request);
        if (raw is null || !tokens.TryValidate(raw, gw.SigningKey, out var t) || t!.Op != FileTokenOp.Upload)
            return Results.Unauthorized();

        if (gw.AllowedContentTypes.Count > 0 && t.ContentType is not null &&
            !gw.AllowedContentTypes.Contains(t.ContentType, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new { Error = "content_type_not_allowed" });

        if (request.ContentLength is long len && len > gw.MaxBytes)
            return Results.BadRequest(new { Error = "too_large" });

        using var limited = new LimitedStream(request.Body, gw.MaxBytes);
        try
        {
            await storage.SaveAsync(limited, t.StorageKey, t.ContentType, ct);
        }
        catch (PayloadTooLargeException)
        {
            return Results.BadRequest(new { Error = "too_large" });
        }

        return Results.Ok();
    }

    public static async Task<IResult> DownloadAsync(
        string token, FileAccessTokenService tokens, IAttachmentStorage storage, FileGatewayOptions gw, CancellationToken ct)
    {
        if (!tokens.TryValidate(token, gw.SigningKey, out var t) || t!.Op != FileTokenOp.Download)
            return Results.Unauthorized();
        var stream = await storage.OpenReadAsync(t.StorageKey, ct);
        return Results.File(stream, t.ContentType ?? "application/octet-stream", t.FileName);
    }

    public static async Task<IResult> DeleteAsync(
        string? token, HttpRequest request, FileAccessTokenService tokens,
        IAttachmentStorage storage, FileGatewayOptions gw, CancellationToken ct)
    {
        var raw = token ?? BearerToken(request);
        if (raw is null || !tokens.TryValidate(raw, gw.SigningKey, out var t) || t!.Op != FileTokenOp.Delete)
            return Results.Unauthorized();
        await storage.DeleteAsync(t.StorageKey, ct);
        return Results.Ok();
    }

    private static string? BearerToken(HttpRequest request)
    {
        var h = request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..] : null;
    }
}
