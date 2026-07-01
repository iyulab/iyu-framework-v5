namespace Iyu.MainServer.Identity;

/// <summary>Computes least-privilege permission scoping: a client can never exceed its owner's permissions.</summary>
public static class PermissionScope
{
    public static IReadOnlyList<string> Effective(IEnumerable<string> requested, IEnumerable<string> ownerPermissions)
    {
        var owner = ownerPermissions.ToHashSet(StringComparer.Ordinal);
        return requested.Where(owner.Contains).Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> Exceeding(IEnumerable<string> requested, IEnumerable<string> ownerPermissions)
    {
        var owner = ownerPermissions.ToHashSet(StringComparer.Ordinal);
        return requested.Where(p => !owner.Contains(p)).Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
