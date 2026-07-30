namespace Iyu.FileServer;

/// <summary>Gateway behavior. SigningKey must match the metadata owner (MainServer) that mints tokens.</summary>
/// <remarks>CORS is owned by the consuming web host (<c>AddCors</c>/<c>UseCors</c>), not this options object.</remarks>
public sealed class FileGatewayOptions
{
    public string SigningKey { get; set; } = default!;

    /// <summary>Maximum accepted upload size in bytes. Default 50MB, sized for <em>document attachments</em>:
    /// an upload is a single request with no resume, so a large transfer restarts from zero if the connection
    /// drops. Raising this for bulk media is not sufficient on its own.
    /// <para>This value is the single authority on the gateway's endpoint — the upload handler aligns the
    /// server's per-request body limit to it, so the host's global limit (30,000,000 bytes by default on
    /// Kestrel, HTTP.sys and IIS in-process) does not silently cap uploads below it.</para>
    /// <para>Ceilings outside the gateway's reach must be configured by the operator: IIS out-of-process
    /// hosting (<c>maxAllowedContentLength</c>, also 30,000,000 by default) and any reverse proxy in front of
    /// the host.</para></summary>
    public long MaxBytes { get; set; } = 50L * 1024 * 1024;
    /// <summary>Content types the gateway accepts for upload. Empty (the default) allows all.
    /// <para>This checks the content type <em>declared by the signed token</em>, not the bytes that arrive —
    /// it enforces a policy the token minter has already committed to, and does not sniff or validate the
    /// payload. Treating it as a guarantee that the stored bytes really are of that type is a mistake.</para>
    /// <para>Fails closed: once this is non-empty, a token that declares no content type is rejected
    /// (<c>content_type_required</c>) rather than waved through, since a check that cannot be evaluated is
    /// not a check that passed.</para></summary>
    public IReadOnlyCollection<string> AllowedContentTypes { get; set; } = Array.Empty<string>();

    /// <summary>Path the gateway's PUT/GET/DELETE endpoints are mapped at.</summary>
    public string RoutePrefix { get; set; } = "/files";
}
