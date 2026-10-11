namespace Iyu.MainServer.Identity;

/// <summary>Computes least-privilege permission scoping: a client can never exceed its owner's permissions.</summary>
/// <remarks>
/// The owner holds what their codes imply as well (<see cref="PermissionHierarchy"/>) — an owner with
/// <c>orders.write</c> may give a client <c>orders.read</c> when the one implies the other.
/// </remarks>
public static class PermissionScope
{
    public static IReadOnlyList<string> Effective(IEnumerable<string> requested, IEnumerable<string> ownerPermissions,
        PermissionHierarchy hierarchy)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        var owner = hierarchy.Expand(ownerPermissions).ToHashSet(StringComparer.Ordinal);
        return requested.Where(owner.Contains).Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> Exceeding(IEnumerable<string> requested, IEnumerable<string> ownerPermissions,
        PermissionHierarchy hierarchy)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        var owner = hierarchy.Expand(ownerPermissions).ToHashSet(StringComparer.Ordinal);
        return requested.Where(p => !owner.Contains(p)).Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
