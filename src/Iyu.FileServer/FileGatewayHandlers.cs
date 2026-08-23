using Iyu.Core.Attachments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Iyu.FileServer;

/// <summary>Static gateway handlers (separated from routing for unit testability). Trust the signed token only — no DB.</summary>
/// <remarks>Every handler takes an optional <see cref="ILogger"/>. A status code alone cannot tell an operator
/// <em>which</em> of the gateway's rejections fired — three distinct conditions all answer 400 — so the reason
/// is logged where it is decided. Nothing logs the raw token: it is a bearer credential, and a log sink is not
/// a place to put one. Successful transfers are not logged either; that is the host's request log.</remarks>
public static class FileGatewayHandlers
{
    public static async Task<IResult> UploadAsync(
        HttpRequest request, string? token, FileAccessTokenService tokens,
        IAttachmentStorage storage, FileGatewayOptions gw, CancellationToken ct, ILogger? logger = null)
    {
        var raw = token ?? BearerToken(request);
        if (raw is null || !tokens.TryValidate(raw, gw.SigningKey, out var t) || t!.Op != FileTokenOp.Upload)
            return Unauthorized(logger, FileTokenOp.Upload, raw is not null);

        // Deliberately here — after the token proves out, before any of the checks below. Placing it after
        // them would avoid raising the limit for requests that are about to be refused, but those refusals
        // return without reading the body, and a refused request's leftover bytes are drained against the
        // limit set here. MaxBytes is the host's own configured ceiling, so draining up to it is bounded and
        // intended; what matters is that an unauthenticated caller never reaches this line.
        AlignServerBodyLimit(request, gw.MaxBytes);

        if (gw.AllowedContentTypes.Count > 0)
        {
            // Fail closed. This check used to be skipped entirely when the token carried no content type,
            // so a host that had configured an allowlist silently accepted everything as soon as one token
            // was minted without one — a check that cannot be evaluated must not be treated as passed.
            if (t.ContentType is null)
            {
                // Warning, not Information: unlike the rejections below this is not something a caller did,
                // it is a mint-side omission that will reject every upload until someone changes code.
                logger?.LogWarning(
                    "File gateway rejected an upload: an allowlist of {AllowedCount} content type(s) is configured, " +
                    "but the token for {StorageKey} declares none, so the allowlist cannot be evaluated. " +
                    "The token minter must set FileAccessToken.ContentType.",
                    gw.AllowedContentTypes.Count, t.StorageKey);
                return Results.BadRequest(new { Error = "content_type_required" });
            }
            if (!IsAllowedContentType(gw.AllowedContentTypes, t.ContentType))
            {
                logger?.LogInformation(
                    "File gateway rejected an upload of {StorageKey}: content type {ContentType} is not in the allowlist.",
                    t.StorageKey, t.ContentType);
                return Results.BadRequest(new { Error = "content_type_not_allowed" });
            }
        }

        if (request.ContentLength is long len && len > gw.MaxBytes)
        {
            logger?.LogInformation(
                "File gateway rejected an upload of {StorageKey}: declared {DeclaredBytes} bytes exceeds MaxBytes {MaxBytes}.",
                t.StorageKey, len, gw.MaxBytes);
            return TooLarge();
        }

        using var limited = new LimitedStream(request.Body, gw.MaxBytes);
        try
        {
            await storage.SaveAsync(limited, t.StorageKey, t.ContentType, ct);
        }
        catch (PayloadTooLargeException)
        {
            logger?.LogInformation(
                "File gateway rejected an upload of {StorageKey}: the body exceeded MaxBytes {MaxBytes} mid-stream " +
                "(no Content-Length, or a header that understated the body).", t.StorageKey, gw.MaxBytes);
            return TooLarge();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Same overrun, noticed by the host instead of us. Once the server limit is aligned to MaxBytes both
            // guards trip on the same byte and the host's wins, because it wraps the stream LimitedStream reads
            // from. The status is already the one we would return; catching it adds the structured body, so an
            // upload sending no Content-Length (chunked) still gets an error a consumer can branch on instead
            // of a bare 413 indistinguishable from an infrastructure rejection.
            logger?.LogInformation(
                "File gateway rejected an upload of {StorageKey}: the host's body-size guard tripped at MaxBytes {MaxBytes}.",
                t.StorageKey, gw.MaxBytes);
            return TooLarge();
        }

        return Results.Ok();
    }

    public static async Task<IResult> DownloadAsync(
        string token, FileAccessTokenService tokens, IAttachmentStorage storage, FileGatewayOptions gw,
        CancellationToken ct, ILogger? logger = null)
    {
        if (!tokens.TryValidate(token, gw.SigningKey, out var t) || t!.Op != FileTokenOp.Download)
            return Unauthorized(logger, FileTokenOp.Download, presented: true);
        var stream = await storage.OpenReadAsync(t.StorageKey, ct);
        if (stream is null)
        {
            // Absence is a normal state of the storage contract (delete racing a still-valid token, orphan
            // sweep), so it is the resource that is missing — not the gateway that has failed. Reporting it
            // as 5xx would misclassify it as an outage in any host that alerts on server errors.
            logger?.LogInformation(
                "File gateway served 404 for {StorageKey}: the token is valid but nothing is stored there.",
                t.StorageKey);
            return Results.NotFound();
        }

        // Range processing lets a large download resume instead of restarting, and lets a media client seek.
        // Both backends hand back a seekable stream, which is what the framework needs to serve a partial
        // response; a host whose backend does not will simply have its Range requests ignored.
        return Results.File(stream, t.ContentType ?? "application/octet-stream", t.FileName,
            enableRangeProcessing: true);
    }

    /// <summary>HEAD counterpart of <see cref="DownloadAsync"/> — answers whether a storage key holds an
    /// object, without transferring its bytes. Reuses the <see cref="FileTokenOp.Download"/> token: being
    /// allowed to know a key exists is not a stronger claim than being allowed to read it, so a separate
    /// token operation would only add friction for callers that mint one token and want both questions
    /// answered from it.</summary>
    public static async Task<IResult> ExistsAsync(
        string token, FileAccessTokenService tokens, IAttachmentStorage storage, FileGatewayOptions gw,
        CancellationToken ct, ILogger? logger = null)
    {
        if (!tokens.TryValidate(token, gw.SigningKey, out var t) || t!.Op != FileTokenOp.Download)
            return Unauthorized(logger, FileTokenOp.Download, presented: true);
        var stream = await storage.OpenReadAsync(t.StorageKey, ct);
        if (stream is null)
        {
            logger?.LogInformation(
                "File gateway served 404 for a HEAD check of {StorageKey}: the token is valid but nothing is stored there.",
                t.StorageKey);
            return Results.NotFound();
        }
        await stream.DisposeAsync();
        return Results.Ok();
    }

    public static async Task<IResult> DeleteAsync(
        string? token, HttpRequest request, FileAccessTokenService tokens,
        IAttachmentStorage storage, FileGatewayOptions gw, CancellationToken ct, ILogger? logger = null)
    {
        var raw = token ?? BearerToken(request);
        if (raw is null || !tokens.TryValidate(raw, gw.SigningKey, out var t) || t!.Op != FileTokenOp.Delete)
            return Unauthorized(logger, FileTokenOp.Delete, raw is not null);
        await storage.DeleteAsync(t.StorageKey, ct);
        return Results.Ok();
    }

    /// <summary>The gateway's single answer to "too many bytes", whichever guard detected it — the header
    /// check, the gateway's own stream ceiling, or the host's. Kept as one helper so the three cannot drift
    /// apart; consumers branch on this payload.
    /// <para>413 rather than 400: the request is well-formed, it is the payload that is unacceptable, and this
    /// is also what the host itself answers when its guard trips first — so a caller sees one status for one
    /// condition regardless of which layer noticed.</para></summary>
    private static IResult TooLarge() =>
        Results.Json(new { Error = "too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>Whether the token's declared content type is in the configured allowlist, comparing
    /// <c>type/subtype</c> only.
    /// <para>A media type's identity is its type and subtype; parameters (<c>; charset=…</c>) are modifiers on
    /// that same type. Comparing whole strings therefore rejects <c>image/jpeg; charset=binary</c> against an
    /// allowlist of <c>image/jpeg</c> — an allowed format refused for a reason the caller cannot see. Nothing
    /// is loosened: type and subtype must still match exactly, and wildcards are not expanded.</para></summary>
    private static bool IsAllowedContentType(IReadOnlyCollection<string> allowlist, string declared)
    {
        var type = MediaTypeOnly(declared);
        foreach (var allowed in allowlist)
            if (string.Equals(MediaTypeOnly(allowed), type, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Strips parameters from a media type. Unparseable input falls back to the trimmed original, so a
    /// malformed value compares exactly as it did before rather than silently matching more.</summary>
    private static string MediaTypeOnly(string value) =>
        MediaTypeHeaderValue.TryParse(value, out var parsed) && parsed.MediaType.HasValue
            ? parsed.MediaType.Value!
            : value.Trim();

    /// <summary>401 plus the one line an operator needs. A 401 here has several causes that look identical from
    /// outside — no token, a bad signature, an expired token, or a token minted for a different operation — and
    /// the gateway cannot currently tell them apart either, so it reports what it does know: which operation was
    /// attempted, and whether a token was presented at all. Expected in normal running (tokens are short-lived),
    /// hence Information rather than Warning. The token itself is never included.</summary>
    private static IResult Unauthorized(ILogger? logger, FileTokenOp op, bool presented)
    {
        logger?.LogInformation(
            "File gateway refused a {Operation} request: {TokenState}.", op,
            presented ? "the token was not valid for this operation, or has expired" : "no token was presented");
        return Results.Unauthorized();
    }

    /// <summary>Aligns the server-enforced body-size limit for <em>this</em> request with the gateway's
    /// <see cref="FileGatewayOptions.MaxBytes"/> — raising it as well as lowering it, so that MaxBytes is the
    /// single authority on the gateway's own endpoint.
    /// <para>Without this, the host's global limit silently caps uploads below MaxBytes: Kestrel, HTTP.sys and
    /// IIS in-process all default to 30,000,000 bytes (~28.6MB), which is under the 50MB gateway default. Bodies
    /// in that gap never reach this handler, so they are rejected by the server as a bare 413 instead of the
    /// gateway's structured <c>too_large</c> — consumers cannot tell the two apart.</para>
    /// <para>Deliberately called only after the token validates: an unauthenticated request must not be able to
    /// raise its own body limit. Must run before the body is read, since the feature turns read-only once it is.
    /// No-op when the host does not expose the feature — notably IIS out-of-process hosting, where IIS's own
    /// <c>maxAllowedContentLength</c> governs and cannot be raised from managed code.</para></summary>
    private static void AlignServerBodyLimit(HttpRequest request, long maxBytes)
    {
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maxBytes;
    }

    private static string? BearerToken(HttpRequest request)
    {
        var h = request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..] : null;
    }
}
