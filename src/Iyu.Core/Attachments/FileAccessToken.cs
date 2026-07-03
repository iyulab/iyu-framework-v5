namespace Iyu.Core.Attachments;

/// <summary>Operation a file-access token authorizes.</summary>
public enum FileTokenOp { Upload, Download, Delete }

/// <summary>Short-lived claim minted by the metadata owner (MainServer) and validated by the file gateway (FileServer). storage_key is signed so the gateway cannot be redirected.</summary>
public sealed record FileAccessToken(
    Guid AttachmentId,
    string StorageKey,
    FileTokenOp Op,
    string? FileName,
    string? ContentType,
    DateTimeOffset ExpiresAt);
