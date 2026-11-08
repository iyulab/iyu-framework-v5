using System.Security.Claims;
using Iyu.Ml;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer.Ml;

/// <summary>
/// The ML layer's <see cref="IMlReadGuard"/> for a host that serves its entities through <c>Iyu.MainServer</c>: a dataset
/// honours the read policies the host declared there (<c>Authorize</c> and <c>AuthorizeProperty</c>) instead of every
/// dataset author re-reading them by hand.
/// </summary>
/// <remarks>
/// <para>
/// The policies are read from two places, because they live in two: the host's declarations (<see cref="IyuMainServerOptions"/>),
/// which hold every policy whatever surface serves the type — including a type only the GraphQL surface serves, which never
/// reaches the OData registry — and the registry of served pairs, which links the read type a caller sees to the write type
/// behind it. A dataset declared over either type, over a base type of one or over a type derived from one, finds the same
/// policies, so reading the table directly is no way around a policy declared on the view over it.
/// </para>
/// <para>
/// A column with a property read policy cannot be a feature or the label, whoever exports: the dataset is a copy that outlives the
/// request, and a policy that some callers satisfy cannot follow it. A related set that names no read policy is held to the host's
/// fallback policy, as a request to the read surface would be — pair by pair, so another related set's policy does not stand in
/// for it. A type the host declared nothing for is held to the fallback policy only.
/// </para>
/// </remarks>
public sealed class MainServerMlReadGuard(
    IyuMainServerOptions options,
    IyuEntityPairRegistry registry,
    IAuthorizationService authorization,
    IAuthorizationPolicyProvider policies) : IMlReadGuard
{
    /// <inheritdoc />
    public string? RefusalFor(Type entity, string property)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);

        foreach (var holder in HoldersOver(entity))
            if (holder.PropertyPolicies.TryGetValue(property, out var policy))
                return $"the read policy '{policy}' guards it on '{holder.Name}', and a dataset is a copy that outlives the request";
        return null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MayReadAsync(Type entity, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(caller);

        var holders = HoldersOver(entity);
        foreach (var policy in holders.Select(h => h.ReadPolicy).OfType<string>().Distinct(StringComparer.Ordinal))
            if (!(await authorization.AuthorizeAsync(caller, policy).ConfigureAwait(false)).Succeeded)
                return false;

        // Pair by pair: a related set that names no read policy is under the host's fallback, as it is for a request to the read
        // surface — and that holds even when another related set names one, because satisfying that one says nothing about this.
        // No related set at all is the same: the host declared nothing for the rows, so the fallback is all there is.
        if ((holders.Count == 0 || holders.Any(h => h.ReadPolicy is null))
            && await policies.GetFallbackPolicyAsync().ConfigureAwait(false) is { } fallback
            && !(await authorization.AuthorizeAsync(caller, resource: null, fallback.Requirements).ConfigureAwait(false)).Succeeded)
            return false;
        return true;
    }

    // Policies are keyed by the exact type a surface serves, but the rows are the same rows when one type derives from the other:
    // a dataset over a base type reads the rows of every derived type that is served, and a dataset over a derived type reads
    // what its base's policy guards. So a holder applies when its type is the entity, a base of it, or derived from it. A served
    // pair also holds the write type behind its read type, and a dataset over that table finds the read type's policies.
    private List<Holder> HoldersOver(Type entity)
    {
        var holders = new List<Holder>();

        foreach (var pair in registry.All.Where(p => Related(p.ReadType, entity) || (p.WriteType is { } write && Related(write, entity))))
            holders.Add(new Holder(pair.SetName, pair.ReadPolicy, pair.PropertyReadPolicies));

        // The declarations themselves: they also hold the types only an optional surface serves. Such a surface says which table
        // stands behind each read type it serves, so a dataset over that table finds the policies declared on the read type.
        var linked = options.Surfaces.SelectMany(surface => surface.WriteTypes)
            .Where(pair => Related(pair.WriteType, entity))
            .Select(pair => pair.ReadType)
            .ToHashSet();
        var declared = options.EntityPolicies.Keys.Concat(options.PropertyPolicies.Keys.Select(k => k.ReadType)).Distinct();
        foreach (var readType in declared.Where(t => Related(t, entity) || linked.Contains(t)))
        {
            var props = options.PropertyPolicies
                .Where(p => p.Key.ReadType == readType)
                .ToDictionary(p => p.Key.Property, p => p.Value, StringComparer.Ordinal);
            holders.Add(new Holder(readType.Name, options.EntityPolicies.TryGetValue(readType, out var policy) ? policy.Read : null, props));
        }
        return holders;
    }

    private static bool Related(Type served, Type entity) =>
        served == entity || served.IsAssignableFrom(entity) || entity.IsAssignableFrom(served);

    private sealed record Holder(string Name, string? ReadPolicy, IReadOnlyDictionary<string, string> PropertyPolicies);
}

/// <summary>Registers the guard.</summary>
public static class IyuMainServerMlExtensions
{
    /// <summary>
    /// Makes ML datasets honour the read policies declared on <c>Iyu.MainServer</c>. Call it where <c>AddIyuMl</c> is
    /// called; such a host does not say <c>NoReadPolicies()</c>, because it has read policies and this is how they apply.
    /// A host has one guard: a second registration is refused rather than one of the two being silently ignored.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <exception cref="InvalidOperationException">A read guard is already registered.</exception>
    public static IServiceCollection AddIyuMlMainServerReadGuard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(IMlReadGuard)))
            throw new InvalidOperationException("A read guard is already registered; a host answers for its read policies in one place.");
        // Scoped: authorization handlers may depend on the request's own services, which a singleton would hold on to.
        services.AddScoped<IMlReadGuard, MainServerMlReadGuard>();
        return services;
    }
}
