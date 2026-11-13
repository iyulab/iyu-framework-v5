using Azure.Core;

namespace Iyu.FileServer.Azure;

/// <summary>
/// Azure Blob backend config — one of two ways to authenticate, never both:
/// <list type="bullet">
/// <item><see cref="ConnectionString"/>: an account key or a SAS in a connection string.</item>
/// <item><see cref="ServiceUri"/> with a <see cref="Credential"/>: a token credential the host supplies (a managed identity
/// or any other <see cref="TokenCredential"/>), for a storage account that has shared-key access turned off.</item>
/// </list>
/// </summary>
public sealed class AzureBlobOptions
{
    /// <summary>A storage connection string (account key or SAS). Leave unset when <see cref="ServiceUri"/> is used.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The blob service endpoint, e.g. <c>https://account.blob.core.windows.net</c>. Needs <see cref="Credential"/>.</summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>The credential used with <see cref="ServiceUri"/>. The host chooses it, so this package takes no dependency on one.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>The container the gateway stores in; created on first use when it does not exist.</summary>
    public string ContainerName { get; set; } = "attachments";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ContainerName))
            throw new ArgumentException("AzureBlobOptions.ContainerName must name a container.", nameof(ContainerName));

        var byKey = !string.IsNullOrWhiteSpace(ConnectionString);
        var byToken = ServiceUri is not null || Credential is not null;
        if (byKey == byToken)
            throw new InvalidOperationException(byKey
                ? "AzureBlobOptions sets both ConnectionString and ServiceUri/Credential — choose one way to authenticate."
                : "AzureBlobOptions needs either ConnectionString, or ServiceUri together with Credential.");
        if (!byToken) return;

        if (ServiceUri is null || Credential is null)
            throw new InvalidOperationException("AzureBlobOptions.ServiceUri and AzureBlobOptions.Credential must be set together.");
        if (!ServiceUri.IsAbsoluteUri)
            throw new InvalidOperationException("AzureBlobOptions.ServiceUri must be an absolute URI.");
    }

    /// <summary>The container's address under <see cref="ServiceUri"/>.</summary>
    internal Uri ContainerUri() =>
        new($"{ServiceUri!.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{Uri.EscapeDataString(ContainerName)}");
}
