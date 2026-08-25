namespace Iyu.DocConvert;

/// <summary>Config for the Gotenberg-backed <see cref="IDocumentConverter"/>. Gotenberg
/// (https://gotenberg.dev) is a self-hosted, MIT-licensed HTTP wrapper around LibreOffice — an
/// on-prem-friendly Docker service, not a commercial dependency: <c>docker run --rm -p 3000:3000
/// gotenberg/gotenberg:8</c>.</summary>
public sealed class GotenbergOptions
{
    /// <summary>Base URL of the Gotenberg instance, e.g. <c>http://localhost:3000</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>Per-request timeout. LibreOffice conversion of a large document can take a while,
    /// so this is deliberately more generous than <see cref="HttpClient"/>'s 100-second default.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
}
