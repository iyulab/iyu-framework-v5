namespace Iyu.Core.Attachments;

/// <summary>Short-lived claim minted by the metadata owner (MainServer) and validated by the file gateway (FileServer). storage_key is signed so the gateway cannot be redirected.</summary>
public sealed record FileAccessToken(
    Guid AttachmentId,
    string StorageKey,
    FileTokenOp Op,
    string? FileName,
    string? ContentType,
    DateTimeOffset ExpiresAt);
