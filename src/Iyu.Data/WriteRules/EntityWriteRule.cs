using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Iyu.Data.WriteRules;

/// <summary>
/// A rule the framework runs against every entity being saved, whatever opened the write —
/// OData PATCH, a GraphQL mutation, a hand-written endpoint, a background job.
/// </summary>
/// <remarks>
/// Derive from <see cref="EntityWriteRule{TEntity}"/> rather than implementing this directly;
/// this interface exists so the dispatcher can hold rules for different entity types in one list.
/// </remarks>
public interface IEntityWriteRule
{
    /// <summary>The entity type this rule applies to.</summary>
    Type EntityType { get; }

    /// <summary>Runs the rule against one tracked entry. Called only for <see cref="EntityType"/>.</summary>
    void Apply(EntityEntry entry, DbContext context);
}

/// <summary>
/// A cross-cutting rule on <typeparamref name="TEntity"/>'s writes — a conditional field lock, a
/// derived value, a normalization, a change log.
/// </summary>
/// <typeparam name="TEntity">The entity this rule guards.</typeparam>
/// <remarks>
/// <para>
/// The framework opens generic write paths (OData PATCH, GraphQL) over registered entities, so a
/// rule enforced at one entry point is bypassed by the next one added. Rules here run at save
/// time, which is *after* every entry point converges — that is the only place a "this field
/// cannot change once X" invariant actually holds.
/// </para>
/// <para>
/// The rule's *content* is the application's; this type is only the place to put it and the
/// pipeline that runs it. Throw from <see cref="Apply(WriteRuleContext{TEntity})"/> to reject a
/// write — the exception propagates out of <c>SaveChanges</c> unchanged, so an app keeps its own
/// domain exception type and whatever maps it to a response.
/// </para>
/// <example>
/// <code>
/// public sealed class OrderVatTypeLock : EntityWriteRule&lt;Order&gt;
/// {
///     protected override void Apply(WriteRuleContext&lt;Order&gt; ctx)
///     {
///         if (!ctx.IsModified(nameof(Order.VatType))) return;
///         if (ctx.Entity.BilledDate is not null)
///             throw new DomainRuleException("...") { Code = "order_vat_type_locked_after_billing" };
///     }
/// }
/// </code>
/// Register every rule in an assembly with <c>services.AddIyuWriteRules(typeof(Program).Assembly)</c>.
/// </example>
/// </remarks>
public abstract class EntityWriteRule<TEntity> : IEntityWriteRule
    where TEntity : class
{
    /// <inheritdoc />
    public Type EntityType => typeof(TEntity);

    /// <inheritdoc />
    void IEntityWriteRule.Apply(EntityEntry entry, DbContext context)
        // The tracker hands out the non-generic EntityEntry, which is not castable to the typed
        // one — they are separate wrappers over the same internal entry. Asking the context for
        // the entity returns the typed wrapper over that same state, so this re-resolves rather
        // than re-tracks: State, OriginalValues and IsModified are all the ones already computed.
        => Apply(new WriteRuleContext<TEntity>(context.Entry((TEntity)entry.Entity), context));

    /// <summary>
    /// Runs against one entry being inserted or updated. Deletes are not dispatched — a rule about
    /// a field's value has nothing to say about a row on its way out.
    /// </summary>
    protected abstract void Apply(WriteRuleContext<TEntity> context);
}

/// <summary>
/// What a rule gets: the tracked entry, the entity, whether this is an insert or an update, and
/// the context — enough to read the previous value or write a log row without re-deriving any of it.
/// </summary>
/// <param name="Entry">The tracked entry being saved.</param>
/// <param name="Db">
/// The context performing the save. Rules that record history add their rows here; they are picked
/// up by the same <c>SaveChanges</c> because the change tracker is still open at this point.
/// </param>
public readonly record struct WriteRuleContext<TEntity>(EntityEntry<TEntity> Entry, DbContext Db)
    where TEntity : class
{
    /// <summary>The entity being saved.</summary>
    public TEntity Entity => Entry.Entity;

    /// <summary>This is a new row.</summary>
    public bool IsAdded => Entry.State == EntityState.Added;

    /// <summary>This is an existing row being updated.</summary>
    public bool IsUpdated => Entry.State == EntityState.Modified;

    /// <summary>
    /// Whether <paramref name="propertyName"/> is being written by this save.
    /// <b>Always <see langword="true"/> on an insert</b> — every property of a new row is being
    /// written — so a lock guarding an edit should test <see cref="IsUpdated"/> first.
    /// </summary>
    public bool IsModified(string propertyName)
        => IsAdded || Entry.Property(propertyName).IsModified;

    /// <summary>
    /// The value <paramref name="propertyName"/> had before this save, or <see langword="default"/>
    /// on an insert (there is no previous value).
    /// </summary>
    public TValue? Original<TValue>(string propertyName)
        => IsAdded ? default : (TValue?)Entry.Property(propertyName).OriginalValue;

    /// <summary>The value <paramref name="propertyName"/> will be written with.</summary>
    public TValue? Current<TValue>(string propertyName)
        => (TValue?)Entry.Property(propertyName).CurrentValue;
}
