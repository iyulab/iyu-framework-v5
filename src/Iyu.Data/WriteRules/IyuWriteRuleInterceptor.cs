using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Iyu.Data.WriteRules;

/// <summary>
/// Runs every registered <see cref="IEntityWriteRule"/> against the entries a save is about to
/// write. Wired by <c>AddIyuMainServer</c>; with no rules registered it does nothing.
/// </summary>
/// <remarks>
/// One interceptor for all rules, rather than one per rule: the per-rule shape makes every rule
/// re-implement the same sync/async override pair and <c>ChangeTracker.Entries&lt;T&gt;()</c> walk,
/// and — the part that actually bites — makes registration a hand-written line per rule that
/// fails silently when forgotten. Here a rule is either discovered or it is not, and
/// <c>AddIyuWriteRules</c> discovers by scanning.
/// </remarks>
public sealed class IyuWriteRuleInterceptor(IEnumerable<IEntityWriteRule> rules) : SaveChangesInterceptor
{
    private readonly List<IEntityWriteRule> _rules = [.. rules ?? []];

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ApplyRules(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyRules(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyRules(DbContext? context)
    {
        if (context is null || _rules.Count == 0) return;

        // Materialized before the loop: a rule that derives a value or writes a log row changes
        // the tracker, and enumerating it lazily while it is being mutated throws. Rules do not
        // see entries created by rules — one pass, so a rule cannot depend on another having
        // already run, which is what keeps their order from becoming a hidden contract.
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        foreach (EntityEntry entry in entries)
        {
            var entityType = entry.Entity.GetType();
            foreach (var rule in _rules)
            {
                // IsAssignableFrom, not ==, so a rule on a base type covers its derived entities
                // the same way a rule written against the concrete type does.
                if (rule.EntityType.IsAssignableFrom(entityType))
                    rule.Apply(entry, context);
            }
        }
    }
}
