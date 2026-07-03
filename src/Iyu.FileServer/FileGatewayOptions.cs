namespace Iyu.FileServer;

/// <summary>Gateway behavior. SigningKey must match the metadata owner (MainServer) that mints tokens.</summary>
public sealed class FileGatewayOptions
{
    public string SigningKey { get; set; } = default!;
    public long MaxBytes { get; set; } = 50L * 1024 * 1024;
    public IReadOnlyCollection<string> AllowedContentTypes { get; set; } = Array.Empty<string>(); // empty = allow all
    public string RoutePrefix { get; set; } = "/files";
    public string[] CorsOrigins { get; set; } = Array.Empty<string>();
}
