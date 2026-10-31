using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iyu.Uncad;

/// <summary>Turns reading drawings through <c>uncad</c> on for a host.</summary>
public static class IyuUncadServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IUncadRunner"/> (as <see cref="UncadRunner"/>) and <see cref="DrawingProbeService"/> as singletons,
    /// configured by <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// Calling this is what turns the module on: nothing is registered until it is. A host that calls it without
    /// <see cref="IyuUncadOptions.ExePath"/>, or with a limit that is not positive, does not start (the message names the setting,
    /// never its value). Whether the executable exists is not checked at startup; see <see cref="IyuUncadOptions.ExePath"/>.
    /// The program is a separate GPL-3.0-or-later work that the host installs; this package does not ship it.
    /// </remarks>
    public static IServiceCollection AddIyuUncad(this IServiceCollection services, Action<IyuUncadOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuUncadOptions>, IyuUncadOptionsValidator>();
        services.AddOptions<IyuUncadOptions>().ValidateOnStart();

        services.AddSingleton<IUncadRunner, UncadRunner>();
        services.AddSingleton<DrawingProbeService>();
        return services;
    }
}
