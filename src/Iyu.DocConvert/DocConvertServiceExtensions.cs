using Microsoft.Extensions.DependencyInjection;

namespace Iyu.DocConvert;

public static class DocConvertServiceExtensions
{
    /// <summary>Registers <see cref="IDocumentConverter"/> backed by a Gotenberg instance, via
    /// <c>IHttpClientFactory</c> (typed client) — no direct <see cref="HttpClient"/> lifetime
    /// management is needed by the host.</summary>
    public static IServiceCollection AddIyuDocConvert(
        this IServiceCollection services,
        Action<GotenbergOptions>? configure = null)
    {
        var options = new GotenbergOptions();
        configure?.Invoke(options);
        Validate(options, nameof(configure));

        services.AddSingleton(options);
        services.AddHttpClient<IDocumentConverter, GotenbergDocumentConverter>(client =>
        {
            // HttpClient only combines a relative request URI with BaseAddress when BaseAddress
            // ends in '/' — normalized here so a host-supplied "http://host:3000" (no trailing
            // slash, the natural way to type it) still works.
            var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = options.Timeout;
        });

        return services;
    }

    // Both values otherwise fail late and in the wrong shape. A base URL without a scheme still
    // parses — "localhost:3000" reads as the scheme "localhost" — and the first conversion then
    // throws the NotSupportedException the contract reserves for an unhandled content type, so a
    // configuration mistake reads as a document one. A non-positive timeout throws only when the
    // first typed client is created.
    private static void Validate(GotenbergOptions options, string paramName)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"GotenbergOptions.BaseUrl must be an absolute http or https URL, such as \"http://localhost:3000\" — got \"{options.BaseUrl}\".",
                paramName);
        }

        if (options.Timeout <= TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException(
                $"GotenbergOptions.Timeout must be positive or Timeout.InfiniteTimeSpan — got {options.Timeout}.",
                paramName);
        }
    }
}
