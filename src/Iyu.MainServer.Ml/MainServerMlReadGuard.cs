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
/// The policies belong to a served pair — the read type a caller sees and the write type behind it. A dataset declared over
/// either type finds the pair, so reading the table directly is no way around a policy declared on the view over it. Two
/// pairs over one write type both apply, and so does a pair whose type is a base of the dataset's entity or derived from it:
/// those are the same rows reached through another type.
/// </para>
/// <para>
/// A column with a property read policy cannot be a feature or the label, whoever exports: the dataset is a copy that outlives the
/// request, and a policy that some callers satisfy cannot follow it. An entity with no read policy of its own is held to the host's
/// fallback policy, as a request to the read surface would be. An entity the host does not serve at all has no declared policy; it
/// is held to the fallback policy only.
/// </para>
/// </remarks>
public sealed class MainServerMlReadGuard(
    IyuEntityPairRegistry registry,
    IAuthorizationService authorization,
    IAuthorizationPolicyProvider policies) : IMlReadGuard
{
    /// <inheritdoc />
    public string? RefusalFor(Type entity, string property)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);

        foreach (var pair in PairsOver(entity))
            if (pair.PropertyReadPolicies.TryGetValue(property, out var policy))
                return $"the read policy '{policy}' guards it on the entity set '{pair.SetName}', and a dataset is a copy that outlives the request";
        return null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MayReadAsync(Type entity, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(caller);

        var related = PairsOver(entity);
        foreach (var policy in related.Select(p => p.ReadPolicy).OfType<string>().Distinct(StringComparer.Ordinal))
            if (!(await authorization.AuthorizeAsync(caller, policy).ConfigureAwait(false)).Succeeded)
                return false;

        // Pair by pair: a related set that names no read policy is under the host's fallback, as it is for a request to the read
        // surface — and that holds even when another related set names one, because satisfying that one says nothing about this.
        // No related set at all is the same: the host declared nothing for the rows, so the fallback is all there is.
        if ((related.Count == 0 || related.Any(p => p.ReadPolicy is null))
            && await policies.GetFallbackPolicyAsync().ConfigureAwait(false) is { } fallback
            && !(await authorization.AuthorizeAsync(caller, resource: null, fallback.Requirements).ConfigureAwait(false)).Succeeded)
            return false;
        return true;
    }

    // Policies are keyed by the exact type a pair serves, but the rows are the same rows when one type derives from the other:
    // a dataset over a base type reads the rows of every derived type that is served, and a dataset over a derived type reads
    // what its base's policy guards. So a pair applies when either of its types is the entity, a base of it, or derived from it.
    private List<IyuEntityPairRegistry.EntityPair> PairsOver(Type entity) =>
        registry.All.Where(p => Related(p.ReadType, entity) || (p.WriteType is { } write && Related(write, entity))).ToList();

    private static bool Related(Type served, Type entity) =>
        served == entity || served.IsAssignableFrom(entity) || entity.IsAssignableFrom(served);
}

/// <summary>Registers the guard.</summary>
public static class IyuMainServerMlExtensions
{
    /// <summary>
    /// Makes ML datasets honour the read policies declared on <c>Iyu.MainServer</c>. Call it where <c>AddIyuMl</c> is
    /// called; such a host does not say <c>NoReadPolicies()</c>, because it has read policies and this is how they apply.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddIyuMlMainServerReadGuard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMlReadGuard, MainServerMlReadGuard>();
        return services;
    }
}
