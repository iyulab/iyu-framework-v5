using System.Security.Claims;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OData.Edm;

namespace Iyu.UBoard.MainServer;

/// <summary>
/// The board layer's <see cref="IBoardSourceCatalog"/> for a host that serves its entities through <c>Iyu.MainServer</c>: the
/// entity sets, properties and links of the host's OData model, kept to what the caller may read.
/// </summary>
/// <remarks>
/// <para>
/// What the caller may read is what a request to the OData surface would be allowed to read: the set's read policy (or, when the
/// host named none, its fallback policy), and for a property its read policy beside that — the policies declared with
/// <c>Authorize</c> and <c>AuthorizeProperty</c>. The model itself is the same for every caller (<c>$metadata</c> is static); this is
/// the part that is not.
/// </para>
/// <para>
/// A set or property the caller may not read is left out. With <c>includeUnreadable</c> it is listed instead and marked not
/// readable, and nothing else is said: not which policy guards it, not why.
/// </para>
/// </remarks>
public sealed class MainServerBoardSourceCatalog(
    IyuMainServerOptions options,
    IyuEntityPairRegistry registry,
    IAuthorizationService authorization,
    IAuthorizationPolicyProvider policies,
    IOptions<ODataOptions> odata) : IBoardSourceCatalog
{
    /// <inheritdoc />
    public async Task<BoardSourceCatalog> DescribeAsync(ClaimsPrincipal caller, bool includeUnreadable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var model = Model();
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);

        async ValueTask<bool> Satisfies(string policy)
        {
            if (!memo.TryGetValue(policy, out var met))
                memo[policy] = met = (await authorization.AuthorizeAsync(caller, policy).ConfigureAwait(false)).Succeeded;
            return met;
        }

        // A set that names no read policy is under the host's fallback policy, as it is for a request to the OData surface.
        var fallbackMet = new Lazy<Task<bool>>(async () =>
            await policies.GetFallbackPolicyAsync().ConfigureAwait(false) is not { } fallback
            || (await authorization.AuthorizeAsync(caller, resource: null, fallback.Requirements).ConfigureAwait(false)).Succeeded);

        async ValueTask<bool> CanRead(string set) =>
            registry.Find(set) is { ReadPolicy: { } policy } ? await Satisfies(policy) : await fallbackMet.Value;

        var readable = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var set in model.EntityContainer.EntitySets())
            readable[set.Name] = await CanRead(set.Name);

        var entities = new List<SourceEntity>();
        foreach (var set in model.EntityContainer.EntitySets().OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mayRead = readable[set.Name];
            if (!mayRead && !includeUnreadable) continue;

            var propertyPolicies = registry.Find(set.Name)?.PropertyReadPolicies;
            var properties = new List<SourceProperty>();
            foreach (var property in set.EntityType.StructuralProperties())
            {
                var propertyMet = mayRead
                    && (propertyPolicies is null || !propertyPolicies.TryGetValue(property.Name, out var policy) || await Satisfies(policy));
                if (!propertyMet && !includeUnreadable) continue;
                var members = property.Type.Definition is IEdmEnumType enumType ? enumType.Members.Select(m => m.Name).ToList() : null;
                properties.Add(new SourceProperty(property.Name, property.Type.FullName(), property.Type.IsNullable, propertyMet, members));
            }

            var links = new List<SourceLink>();
            foreach (var link in set.EntityType.NavigationProperties())
            {
                var target = set.FindNavigationTarget(link)?.Name ?? link.ToEntityType().Name;
                if (!includeUnreadable && !(readable.TryGetValue(target, out var targetMet) && targetMet)) continue;
                links.Add(new SourceLink(link.Name, target, link.Type.IsCollection()));
            }

            entities.Add(new SourceEntity(set.Name, mayRead, properties, links));
        }

        return new BoardSourceCatalog(entities);
    }

    private IEdmModel Model() =>
        odata.Value.RouteComponents.TryGetValue(options.ODataRoutePrefix, out var route)
            ? route.EdmModel
            : throw new InvalidOperationException("The OData model of Iyu.MainServer is not registered; call AddIyuMainServer before using the board source catalog.");
}

/// <summary>Registers the catalog.</summary>
public static class IyuUBoardMainServerExtensions
{
    /// <summary>
    /// Makes <c>AddIyuUBoard(o =&gt; o.Catalog())</c> describe the host's OData model, kept to what the caller may read. Call it after
    /// <c>AddIyuMainServer</c>. A host has one catalog: a second registration is refused rather than one of the two being ignored.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <exception cref="InvalidOperationException">A source catalog is already registered.</exception>
    public static IServiceCollection AddIyuUBoardMainServerCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(IBoardSourceCatalog)))
            throw new InvalidOperationException("A board source catalog is already registered; a host describes its sources in one place.");
        // Scoped: authorization handlers may depend on the request's own services, which a singleton would hold on to.
        services.AddScoped<IBoardSourceCatalog, MainServerBoardSourceCatalog>();
        return services;
    }
}
