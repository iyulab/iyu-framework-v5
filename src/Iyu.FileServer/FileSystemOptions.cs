namespace Iyu.FileServer;

/// <summary>Local filesystem backend config. Attachment bytes are stored as files beneath <see cref="RootPath"/>,
/// keyed by the (server-authoritative) storage key. Suits on-prem/NAS hosting without Azure Blob.</summary>
public sealed class FileSystemOptions
{
    /// <summary>Absolute root directory under which attachment objects are written. Created if missing.</summary>
    public string RootPath { get; set; } = default!;
}
