using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Iyu.UBoard.IronHive;

/// <summary>Turns the assistant of a board's chat on, answered by IronHive.Agent over a model service the host names.</summary>
public static class IyuUBoardIronHiveExtensions
{
    /// <summary>
    /// Registers the <see cref="IBoardAssistant"/> that <see cref="IyuUBoardEndpoints.MapIyuUBoard"/> serves at
    /// <c>POST {prefix}/boards/{id}/turn</c>. Without this call (or another <see cref="IBoardAssistant"/>) the assistant is off: the route
    /// is not mapped and <c>GET {prefix}/access</c> says so.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Names the model service: its address, key, model and the most a reply may take. Required, and the only place those come from.</param>
    /// <remarks>
    /// A host that names no address or model does not start; the message names what is missing and never a value. A host that wants the
    /// settings from its configuration reads them itself and sets them here, or binds a section with
    /// <c>services.AddOptions&lt;IronHiveAssistantOptions&gt;().Bind(section)</c> before this call. The model service is reached through
    /// IronProw on a service provider of the adapter's own, so it is not the host's <c>IChatClient</c>.
    /// </remarks>
    public static IServiceCollection AddIyuUBoardIronHive(this IServiceCollection services, Action<IronHiveAssistantOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IronHiveAssistantOptions>, IronHiveAssistantOptionsValidator>());
        services.AddOptions<IronHiveAssistantOptions>().ValidateOnStart();
        services.TryAddSingleton<IBoardAssistant, IronHiveBoardAssistant>();
        return services;
    }
}
