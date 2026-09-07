using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Data.WriteRules;

/// <summary>Registers <see cref="IEntityWriteRule"/> implementations by scanning assemblies.</summary>
public static class WriteRuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers every concrete, non-abstract <see cref="IEntityWriteRule"/> in
    /// <paramref name="assemblies"/> as a scoped service, so <c>AddIyuMainServer</c>'s interceptor
    /// picks them up.
    /// </summary>
    /// <remarks>
    /// Scanning rather than a line per rule is the point, not a convenience: a hand-written
    /// registration list fails by *omission*, and an omitted rule is indistinguishable at runtime
    /// from one whose condition never fired — nothing throws, nothing logs, the invariant is just
    /// not enforced. Discovery removes the way to get it wrong.
    /// <para>
    /// Scoped, because a rule may depend on scoped services (the current user, a clock, a
    /// tenant). Idempotent: registering the same rule type twice would run it twice, so a type
    /// already registered is skipped.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">Assemblies to scan. Passing none scans nothing — and is a no-op.</param>
    public static IServiceCollection AddIyuWriteRules(
        this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var type in assemblies.Distinct().SelectMany(SafeGetTypes))
        {
            if (type is not { IsClass: true, IsAbstract: false }) continue;
            if (!typeof(IEntityWriteRule).IsAssignableFrom(type)) continue;
            if (services.Any(d => d.ServiceType == typeof(IEntityWriteRule)
                                  && d.ImplementationType == type)) continue;

            services.AddScoped(typeof(IEntityWriteRule), type);
        }

        return services;
    }

    /// <summary>
    /// Types from an assembly, tolerating a partially-loadable one. A single unloadable type —
    /// typically an unrelated dependency the host never resolves — should not take down the
    /// registration of every rule beside it.
    /// </summary>
    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
