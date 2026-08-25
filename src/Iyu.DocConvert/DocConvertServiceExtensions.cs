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
}
