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
    /// <exception cref="InvalidOperationException">
    /// One of <paramref name="assemblies"/> is only partially loadable, so the rules in it cannot
    /// be enumerated. See <see cref="GetTypesOrRefuse"/> for why that is refused rather than
    /// worked around.
    /// </exception>
    public static IServiceCollection AddIyuWriteRules(
        this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var type in assemblies.Distinct().SelectMany(GetTypesOrRefuse))
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
    /// Types from an assembly, refusing one that is only partially loadable.
    /// </summary>
    /// <remarks>
    /// Tolerating the failure — registering the types that did load and dropping the rest — is
    /// what this used to do, on the grounds that one unloadable type should not take down the
    /// rules beside it. It reintroduced exactly the failure the scan exists to remove: a rule
    /// absent because its type never loaded is indistinguishable at runtime from one whose
    /// condition never fired, so an invariant is simply not enforced and nothing says so.
    /// <para>
    /// No narrower rule is available. <see cref="ReflectionTypeLoadException.LoaderExceptions"/>
    /// names the dependency that could not be resolved, not the types that needed it, so
    /// "tolerate only the dropped types that were not rules" cannot be decided from here. When
    /// the set cannot be known and the missing member would be a guard, the safe answer is to
    /// refuse to start rather than to start unguarded.
    /// </para>
    /// </remarks>
    private static IEnumerable<Type> GetTypesOrRefuse(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            throw new InvalidOperationException(PartialLoadMessage(assembly, ex), ex);
        }
    }

    private static string PartialLoadMessage(Assembly assembly, ReflectionTypeLoadException ex)
    {
        var loaderErrors = ex.LoaderExceptions
            .Where(e => e is not null)
            .Select(e => e!.Message)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var detail = loaderErrors.Length == 0
            ? "  (the runtime reported no loader error detail)"
            : string.Join(Environment.NewLine, loaderErrors.Select(m => "  - " + m));

        return $"""
            Cannot register write rules: '{assembly.GetName().Name}' is only partially loadable,
            so the IEntityWriteRule types in it cannot be enumerated. Registering the ones that
            did load would leave any rule that failed to load silently unenforced, which is the
            failure assembly scanning exists to prevent.

            Loader errors:
            {detail}

            Resolve it either way:
              - pass an assembly whose dependencies the host resolves (rules and what they need), or
              - deploy the missing dependencies alongside the host so the assembly loads fully.
            """;
    }
}
