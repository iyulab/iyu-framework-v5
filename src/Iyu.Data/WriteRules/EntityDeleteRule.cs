using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Iyu.Data.WriteRules;

/// <summary>
/// A rule the framework runs against every entity being deleted, whatever opened the write —
/// OData DELETE, a GraphQL mutation, a hand-written endpoint, a background job.
/// </summary>
/// <typeparam name="TEntity">The entity whose deletion this rule reacts to.</typeparam>
/// <remarks>
/// <para>
/// A delete is the one write a field-value rule (<see cref="EntityWriteRule{TEntity}"/>) never sees,
/// and it is the one a derived record has to follow: a summary, an index entry, a tombstone, a row in
/// another store that was computed from this one. Enforced at one entry point, it is bypassed by the
/// next, so it runs at save time, where every entry point converges.
/// </para>
/// <para>
/// It runs <b>before</b> the save, in the same pass as the other write rules and before the
/// transaction begins. Rows the rule adds or removes through <see cref="WriteRuleContext{TEntity}.Db"/>
/// are saved by the same <c>SaveChanges</c> — atomically with the delete on a relational provider — and
/// throwing refuses the delete (the exception propagates unchanged, as it does for an insert or update).
/// </para>
/// <para>
/// What it is told about, and what it is not:
/// </para>
/// <list type="bullet">
/// <item>Entries the context <b>tracks</b> as deleted — including dependents a cascade marked deleted
/// because they were tracked when their principal was removed.</item>
/// <item>Not rows the database removes on its own (an <c>ON DELETE CASCADE</c> beneath a row the context
/// never loaded) and not <c>ExecuteDelete</c>, which bypasses the change tracker. If a derived record
/// must follow those, key it to the principal and let the principal's own deletion carry it.</item>
/// <item>Not an effect outside the database. The rule runs before the save is known to succeed, so a side
/// effect on another system would happen even when the delete is rolled back. Write the intent as a row
/// through <see cref="WriteRuleContext{TEntity}.Db"/> (an outbox) and act on it after the commit.</item>
/// </list>
/// <example>
/// <code>
/// public sealed class RecordOrderRemoval : EntityDeleteRule&lt;Order&gt;
/// {
///     protected override void OnDeleting(WriteRuleContext&lt;Order&gt; ctx)
///         =&gt; ctx.Db.Add(new OrderRemoval { OrderId = ctx.Entity.Id });
/// }
/// </code>
/// Register it with the rest: <c>services.AddIyuWriteRules(typeof(Program).Assembly)</c>.
/// </example>
/// </remarks>
public abstract class EntityDeleteRule<TEntity> : IEntityWriteRule
    where TEntity : class
{
    /// <inheritdoc />
    public Type EntityType => typeof(TEntity);

    /// <inheritdoc />
    /// <remarks>A delete rule has nothing to say about an insert or an update.</remarks>
    void IEntityWriteRule.Apply(EntityEntry entry, DbContext context) { }

    /// <inheritdoc />
    void IEntityWriteRule.ApplyDelete(EntityEntry entry, DbContext context)
        // Same re-resolution as EntityWriteRule<T>: the typed wrapper over the state the tracker
        // already holds, not a second tracking of the entity.
        => OnDeleting(new WriteRuleContext<TEntity>(context.Entry((TEntity)entry.Entity), context));

    /// <summary>
    /// Runs against one entry being deleted. <see cref="WriteRuleContext{TEntity}.IsDeleted"/> is
    /// <see langword="true"/>; the entity still holds the values it had when it was loaded.
    /// </summary>
    protected abstract void OnDeleting(WriteRuleContext<TEntity> context);
}
