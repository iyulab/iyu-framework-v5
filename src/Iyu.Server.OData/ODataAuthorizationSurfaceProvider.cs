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
            // POST/PATCH — withdrawn together only when both are read-only.
            var mutable = !pair.ReadOnlyVerbs.Contains(ODataVerb.Post)
                       || !pair.ReadOnlyVerbs.Contains(ODataVerb.Patch);
            if (mutable)
            {
                entries.Add(new AuthorizationSurfaceEntry(
                    Surface, pair.SetName, AuthorizationSurfaceOperation.Write, pair.WritePolicy));
            }

            // DELETE reports the policy that actually runs, not the one that was typed: a set with
            // no DeletePolicy is governed by WritePolicy, and reporting `null` there would invent
            // an unprotected row for an app that never asked to separate the two.
            if (!pair.ReadOnlyVerbs.Contains(ODataVerb.Delete))
            {
                entries.Add(new AuthorizationSurfaceEntry(
                    Surface, pair.SetName, AuthorizationSurfaceOperation.Delete,
                    pair.DeletePolicy ?? pair.WritePolicy));
            }
        }

        return entries;
    }
}
