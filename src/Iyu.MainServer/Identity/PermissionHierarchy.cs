namespace Iyu.MainServer.Identity;

/// <summary>
/// One permission code that also counts as holding others — the broader code first:
/// <c>new("orders.write", "orders.read")</c> says whoever holds <c>orders.write</c> may also do what
/// <c>orders.read</c> allows.
/// </summary>
public sealed record PermissionImplication(string Code, params string[] Implies);

/// <summary>
/// Which permission codes count as holding which others, from the implications declared to
/// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuIdentity"/>. Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// Implication is transitive: if <c>admin</c> implies <c>write</c> and <c>write</c> implies <c>read</c>, holding
/// <c>admin</c> satisfies the <c>read</c> policy. The framework applies it in the two places it decides from
/// permission codes — each catalog code's authorization policy, and the rule that a service client never exceeds
/// its owner — so an implied code never has to be assigned alongside the code that implies it.
/// </para>
/// <para>
/// Nothing is expanded into what is issued: a session or token carries the codes it was given, and the policies
/// know what those codes imply. An application that needs the full set a person may act under — to decide what a
/// client screen shows, say — asks <see cref="Expand"/>.
/// </para>
/// </remarks>
public sealed class PermissionHierarchy
{
    /// <summary>No code implies another.</summary>
    public static PermissionHierarchy Flat { get; } = new([], []);

    // code → every code that implies it, transitively (the code itself excluded).
    private readonly Dictionary<string, HashSet<string>> _impliedBy = new(StringComparer.Ordinal);
    // code → every code it implies, transitively (the code itself excluded).
    private readonly Dictionary<string, HashSet<string>> _implies = new(StringComparer.Ordinal);

    /// <param name="catalog">The permission codes the application knows. Every code an implication names must be one
    /// of them — a misspelled code would otherwise imply nothing, silently.</param>
    /// <param name="implications">What each broader code also counts as.</param>
    /// <exception cref="ArgumentException">An implication names a code outside the catalog, a code implies itself, or
    /// two codes imply each other (directly or through others) — two names for one permission.</exception>
    public PermissionHierarchy(IEnumerable<string> catalog, IEnumerable<PermissionImplication> implications)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(implications);
        var known = catalog.ToHashSet(StringComparer.Ordinal);

        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var implication in implications)
        {
            ArgumentNullException.ThrowIfNull(implication, nameof(implications));
            RequireKnown(implication.Code, known, nameof(implications));
            foreach (var implied in implication.Implies ?? [])
            {
                RequireKnown(implied, known, nameof(implications));
                if (StringComparer.Ordinal.Equals(implied, implication.Code))
                    throw new ArgumentException($"Permission '{implied}' cannot imply itself.", nameof(implications));
                if (!direct.TryGetValue(implication.Code, out var set))
                    direct[implication.Code] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(implied);
            }
        }

        foreach (var code in direct.Keys)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(direct[code]);
            while (pending.TryPop(out var next))
            {
                if (StringComparer.Ordinal.Equals(next, code))
                    throw new ArgumentException(
                        $"Permissions '{code}' and '{direct[code].First(d => Reaches(direct, d, code))}' imply each other — "
                        + "they would be two names for one permission.", nameof(implications));
                if (!reached.Add(next)) continue;
                if (direct.TryGetValue(next, out var further))
                    foreach (var f in further) pending.Push(f);
            }
            _implies[code] = reached;
            foreach (var implied in reached)
            {
                if (!_impliedBy.TryGetValue(implied, out var by))
                    _impliedBy[implied] = by = new HashSet<string>(StringComparer.Ordinal);
                by.Add(code);
            }
        }
    }

    /// <summary>The codes whose holder satisfies <paramref name="code"/>: the code itself and every code that implies
    /// it. Sorted.</summary>
    public IReadOnlyList<string> SatisfiedBy(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return (_impliedBy.TryGetValue(code, out var by) ? by.Append(code) : [code])
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    /// <summary>The codes held together with everything they imply. Distinct, sorted.</summary>
    public IReadOnlyList<string> Expand(IEnumerable<string> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in held)
        {
            all.Add(code);
            if (_implies.TryGetValue(code, out var implied)) all.UnionWith(implied);
        }
        return all.OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    private static void RequireKnown(string code, HashSet<string> known, string paramName)
    {
        if (code is null || !known.Contains(code))
            throw new ArgumentException(
                $"Permission implication names '{code}', which is not in the permission catalog.", paramName);
    }

    private static bool Reaches(Dictionary<string, HashSet<string>> direct, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([from]);
        while (pending.TryPop(out var next))
        {
            if (StringComparer.Ordinal.Equals(next, to)) return true;
            if (seen.Add(next) && direct.TryGetValue(next, out var further))
                foreach (var f in further) pending.Push(f);
        }
        return false;
    }
}
