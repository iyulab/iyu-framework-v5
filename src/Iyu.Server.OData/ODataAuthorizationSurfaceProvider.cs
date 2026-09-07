using Iyu.Core.Authorization;

namespace Iyu.Server.OData;

/// <summary>
/// Reports the OData surface from <see cref="IyuEntityPairRegistry"/> — one read entry and one
/// write entry per registered set, carrying whatever
/// <see cref="IyuEntityPairRegistry.RestrictPolicy"/> attached.
/// </summary>
/// <remarks>
/// Nothing here inspects the running endpoint graph, so a policy applied by an MVC convention or
/// an <c>[Authorize]</c> attribute is not visible — the caveat on
/// <see cref="IAuthorizationSurfaceReport"/> covers what that means for a reader.
/// </remarks>
public sealed class ODataAuthorizationSurfaceProvider(IyuEntityPairRegistry registry)
    : IAuthorizationSurfaceProvider
{
    private readonly IyuEntityPairRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public string Surface => "OData";

    /// <inheritdoc />
    public IReadOnlyList<AuthorizationSurfaceEntry> Describe()
    {
        var entries = new List<AuthorizationSurfaceEntry>();
        foreach (var pair in _registry.All.OrderBy(p => p.SetName, StringComparer.Ordinal))
        {
            entries.Add(new AuthorizationSurfaceEntry(
                Surface, pair.SetName, AuthorizationSurfaceOperation.Read, pair.ReadPolicy));

            // A set that refuses every write verb has no write surface to protect — reporting it
            // as unprotected would be a false positive, and a false positive in a list whose whole
            // job is to be empty is what makes people stop reading the list.
            var writable = !Enum.GetValues<ODataVerb>().All(pair.ReadOnlyVerbs.Contains);
            if (writable)
            {
                entries.Add(new AuthorizationSurfaceEntry(
                    Surface, pair.SetName, AuthorizationSurfaceOperation.Write, pair.WritePolicy));
            }
        }

        return entries;
    }
}
