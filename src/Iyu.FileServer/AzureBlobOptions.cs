namespace Iyu.FileServer;

/// <summary>Azure Blob backend config.</summary>
public sealed class AzureBlobOptions
{
    public string ConnectionString { get; set; } = default!;
    public string ContainerName { get; set; } = "attachments";
}
