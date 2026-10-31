using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iyu.UVision;

/// <summary>Turns calling a vision judgment engine on for a host.</summary>
public static class IyuUVisionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IVisionEngine"/> (backed by U-Vision over HTTP) as a typed client of <c>IHttpClientFactory</c>,
    /// configured by <paramref name="configure"/> — the host manages no <see cref="HttpClient"/> lifetime.
    /// </summary>
    /// <remarks>
    /// Calling this is what turns the module on: nothing is registered until it is. A host that calls it without
    /// <see cref="IyuUVisionOptions.BaseUrl"/> (an absolute http or https URL), or with a limit that is not usable, does not
    /// start; the message names the setting, never its value. The engine is a separate service that the host runs; this package only
    /// calls its HTTP interface and neither links to it nor ships it. A host that wants a different engine, or a fake in a test,
    /// registers its own <see cref="IVisionEngine"/> after the call.
    /// </remarks>
    public static IServiceCollection AddIyuUVision(this IServiceCollection services, Action<IyuUVisionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuUVisionOptions>, IyuUVisionOptionsValidator>();
        services.AddOptions<IyuUVisionOptions>().ValidateOnStart();

        // The options are read when the client is created, not now: they are only known to be valid once the host has started
        // and run the validator above.
        services.AddHttpClient<IVisionEngine, UVisionEngine>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<IyuUVisionOptions>>().Value;
            client.BaseAddress = options.BaseAddress();
            client.Timeout = options.Timeout;
        });

        return services;
    }
}
