using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace Iyu.Server.OData;

/// <summary>
/// Generic OData controller providing GET / GET(key) / POST / PATCH / DELETE for
/// an entity pair. Reads query the view-backed <typeparamref name="TRead"/>
/// DbSet, writes persist the table-backed <typeparamref name="TWrite"/> DbSet.
/// </summary>
/// <remarks>
/// <para>
/// Reads and writes share the CLR property-name space via mdd-booster's field
/// duplication strategy (<c>IXxx</c> marker interface, same getter names on
/// both classes). The runtime copies overlapping properties via reflection;
/// extra fields on the read side (lookups/rollups/computed) are silently
/// skipped because they are not part of the write entity's EF model.
/// </para>
/// <para>
/// Consumers typically subclass this with a concrete type pair:
/// <c>public class OrdersController : IyuODataController&lt;OrderExt, Order&gt;</c>.
/// The generic base handles routing through OData conventions on the subclass
/// name. Custom per-entity behavior is added by overriding the virtuals.
/// </para>
/// </remarks>
public abstract class IyuODataController<TRead, TWrite> : ODataController
    where TRead : IyuEntity
    where TWrite : IyuEntity, new()
{
    /// <summary>The EF Core context the controller reads and writes through.</summary>
    protected IyuDbContext Context { get; }

    /// <summary>DbSet backing the read (view) type.</summary>
    protected DbSet<TRead> ReadSet => Context.Set<TRead>();

    /// <summary>DbSet backing the write (table) type.</summary>
    protected DbSet<TWrite> WriteSet => Context.Set<TWrite>();

    protected IyuODataController(IyuDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    /// <summary>
    /// GET — returns the full queryable set. OData query options
    /// (<c>$filter</c>, <c>$orderby</c>, <c>$select</c>, <c>$expand</c>, paging)
    /// are applied by the <c>[EnableQuery]</c> attribute.
    /// </summary>
    [EnableQuery]
    public virtual IQueryable<TRead> Get() => ReadSet.AsNoTracking();

    /// <summary>GET by key — returns a single entity or 404.</summary>
    [EnableQuery]
    public virtual async Task<IActionResult> Get(Guid key, CancellationToken ct)
    {
        var entity = await ReadSet.AsNoTracking().FirstOrDefaultAsync(e => e.Id == key, ct);
        return entity is null ? NotFound() : Ok(entity);
    }

    /// <summary>
    /// POST — creates a new write entity. The request body is bound as
    /// <typeparamref name="TRead"/>, then common properties are copied to a
    /// fresh <typeparamref name="TWrite"/> before persistence. Returns the
    /// created read-side projection.
    /// </summary>
    public virtual async Task<IActionResult> Post(
        [FromBody] TRead body, [FromServices] IyuEntityPairRegistry registry, CancellationToken ct)
    {
        if (ReadOnlyRejection(registry, ODataVerb.Post) is { } rejected) return rejected;
        if (body is null) return BadRequest();
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var write = new TWrite();
        if (body.Id == Guid.Empty) body.Id = Guid.NewGuid();
        write.Id = body.Id;
        CopyCommonProperties(body, write, registry.FindByReadType(typeof(TRead))?.WriteExcludedProperties);

        WriteSet.Add(write);
        await Context.SaveChangesAsync(ct);

        // Return the freshly materialized read row (includes server-assigned timestamps).
        var created = await ReadSet.AsNoTracking().FirstOrDefaultAsync(e => e.Id == write.Id, ct);
        return Created($"{Request.Path}({write.Id})", created ?? (object)write);
    }

    /// <summary>
    /// PATCH — partial update. Loads the existing write row, applies the delta,
    /// validates the properties the client actually sent, persists, and returns
    /// 204 (no body) on success, 400 when a sent value violates the model's
    /// annotations, or 404 if the key is unknown.
    /// </summary>
    /// <remarks>
    /// An unknown key is answered before the payload is looked at, so a request
    /// that is both unknown and invalid is a 404. Both answers are defensible;
    /// what is not defensible is letting statement order decide, so the order is
    /// deliberate and pinned by a test.
    /// </remarks>
    public virtual async Task<IActionResult> Patch(
        Guid key, [FromBody] Delta<TRead> delta, [FromServices] IyuEntityPairRegistry registry, CancellationToken ct)
    {
        if (ReadOnlyRejection(registry, ODataVerb.Patch) is { } rejected) return rejected;
        if (delta is null) return BadRequest();

        var write = await WriteSet.FirstOrDefaultAsync(e => e.Id == key, ct);
        if (write is null) return NotFound();

        // Apply ONLY the properties the client actually set. Copying the full
        // TRead placeholder would overwrite untouched fields with defaults.
        var changedNames = delta.GetChangedPropertyNames().ToHashSet(StringComparer.Ordinal);
        if (changedNames.Count == 0)
            return StatusCode(StatusCodes.Status204NoContent);

        var readProjection = Activator.CreateInstance<TRead>();
        delta.Patch(readProjection);

        if (!ValidateChangedProperties(readProjection, changedNames))
            return BadRequest(ModelState);

        CopySelectedProperties(readProjection, write, changedNames, registry.FindByReadType(typeof(TRead))?.WriteExcludedProperties);
        await Context.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// Validates a partial update: the annotations on <typeparamref name="TRead"/>
    /// are evaluated against the values the client actually sent, and errors
    /// about anything else are discarded. Returns true when nothing is left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole model is validated and the result is then narrowed — the
    /// validator itself is never narrowed.</b> Evaluating attributes property by
    /// property would mean building a second validation pipeline beside MVC's,
    /// and the two disagree about things a caller sees: how a display name is
    /// resolved, how localization applies, how the message is assembled. A
    /// create and a partial update would then reject the same value with
    /// different words. Running the same validator and filtering afterwards is
    /// what makes the two responses identical by construction.
    /// </para>
    /// <para>
    /// <b>The filtering is not tidy-up — it is what makes the update partial.</b>
    /// The projection is a fresh instance with the delta applied on top, so every
    /// required string the client did not send holds its default, which
    /// <c>[Required]</c> rejects. Validating the whole model therefore
    /// <i>always</i> reports errors for unsent required fields. Removing them is
    /// the step that produces partial-update semantics. Anyone who reads this as
    /// redundant and "narrows the validation instead" reintroduces the asymmetry
    /// above.
    /// </para>
    /// <para>
    /// <b>Type-level validation is out of scope for a partial update.</b> A class
    /// level attribute or <c>IValidatableObject</c> reports against no particular
    /// property, so its key matches nothing sent and is discarded. Keeping those
    /// would make an entity with cross-field rules impossible to patch at all,
    /// because the rule would be judged against fields the request never carried.
    /// </para>
    /// </remarks>
    private bool ValidateChangedProperties(TRead projection, ISet<string> changedNames)
    {
        // The delta's own binding state is not what is being judged here, and
        // this action has never consulted it.
        ModelState.Clear();
        TryValidateModel(projection);

        foreach (var key in ModelState.Keys.Where(k => !IsUnderChangedProperty(k, changedNames)).ToList())
            ModelState.Remove(key);

        return ModelState.IsValid;
    }

    /// <summary>
    /// True when a model-state key belongs to one of the sent properties —
    /// either the property itself or something beneath it.
    /// </summary>
    /// <remarks>
    /// Exact-matching the key would discard errors from inside a sent complex
    /// value: patching <c>Address</c> reports failures under <c>Address.City</c>
    /// and <c>Items[0].Name</c>, and those are errors about what was sent.
    /// </remarks>
    private static bool IsUnderChangedProperty(string key, ISet<string> changedNames)
        => changedNames.Contains(key)
           || changedNames.Any(n => key.StartsWith(n + ".", StringComparison.Ordinal)
                                 || key.StartsWith(n + "[", StringComparison.Ordinal));

    /// <summary>DELETE by key.</summary>
    public virtual async Task<IActionResult> Delete(
        Guid key, [FromServices] IyuEntityPairRegistry registry, CancellationToken ct)
    {
        if (ReadOnlyRejection(registry, ODataVerb.Delete) is { } rejected) return rejected;

        var write = await WriteSet.FirstOrDefaultAsync(e => e.Id == key, ct);
        if (write is null) return NotFound();

        WriteSet.Remove(write);
        await Context.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Refuses <paramref name="verb"/> when the entity pair backing this controller
    /// was registered read-only for it, via <c>IyuEdmModelBuilder.AddEntityPair</c>'s
    /// <c>readOnlyVerbs</c> parameter.
    /// </summary>
    /// <remarks>
    /// 405, not 400: the request is well-formed and the resource exists — it is
    /// this specific method that the entity set does not support, which is
    /// exactly what 405 Method Not Allowed means. <c>$metadata</c> advertises the
    /// same restriction via the OData Capabilities vocabulary
    /// (<see cref="IyuEdmModelBuilder"/>), so a client that reads it and one that
    /// does not are rejected identically here — this check does not trust the
    /// client to have read it.
    /// </remarks>
    private ObjectResult? ReadOnlyRejection(IyuEntityPairRegistry registry, ODataVerb verb)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var pair = registry.FindByReadType(typeof(TRead));
        if (pair is null || !pair.ReadOnlyVerbs.Contains(verb)) return null;

        return StatusCode(StatusCodes.Status405MethodNotAllowed,
            $"Entity set '{pair.SetName}' is registered read-only for {verb} and does not accept this request.");
    }

    /// <summary>
    /// Copies overlapping (name + assignable type) properties from
    /// <paramref name="source"/> to <paramref name="target"/>. Used by POST
    /// where every field on the body is intentionally a new value. Nav
    /// properties and collections are naturally excluded because only scalar
    /// writable properties match.
    /// </summary>
    /// <param name="source">The bound request body.</param>
    /// <param name="target">The freshly constructed write entity.</param>
    /// <param name="excluded">
    /// Property names to skip regardless of <paramref name="source"/>/<paramref name="target"/>
    /// overlap — the set's <see cref="IyuEdmModelBuilder.ExcludeFromWrite{T}"/>
    /// marks, if any.
    /// </param>
    protected static void CopyCommonProperties(TRead source, TWrite target, IReadOnlySet<string>? excluded = null)
        => CopySelectedProperties(source, target, filter: null, excluded);

    /// <summary>
    /// Copies a subset of properties from <paramref name="source"/> to
    /// <paramref name="target"/>. When <paramref name="filter"/> is non-null,
    /// only property names present in it are considered. Always skips
    /// <c>Id</c>/<c>CreatedAt</c>/<c>UpdatedAt</c> — those are owned by the
    /// caller (Id) or the interceptor (timestamps) — and any name present in
    /// <paramref name="excluded"/> (<see cref="IyuEdmModelBuilder.ExcludeFromWrite{T}"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A property with no writable counterpart is skipped, and a partial
    /// update that only carried such properties therefore succeeds without
    /// changing anything.</b> That is a recorded choice, not an oversight, and a
    /// test pins it — but it is a weak one, so here is the reasoning.
    /// </para>
    /// <para>
    /// The read type carries fields the write type does not: lookups, rollups
    /// and computed values, which the view produces. A client that reads an
    /// entity, edits one field and sends the whole object back — an ordinary
    /// pattern — carries all of them, and every one is reported as changed.
    /// Rejecting a request because it mentioned a derived property would break
    /// that client for doing nothing wrong. Skipping is what makes it work.
    /// </para>
    /// <para>
    /// The cost is that a request which mentions <em>only</em> such properties
    /// changes nothing and is answered 204, which reads as success. Narrowing
    /// the rejection to that case would keep round-tripping working, but it is
    /// still a behaviour change for callers who patch a computed field today
    /// and get a quiet no-op — so it is a decision to take deliberately with a
    /// consumer in view, not a tidy-up to fold into an unrelated release.
    /// </para>
    /// </remarks>
    protected static void CopySelectedProperties(
        TRead source, TWrite target, ISet<string>? filter, IReadOnlySet<string>? excluded = null)
    {
        var targetProps = typeof(TWrite).GetProperties()
            .Where(p => p.CanWrite && p.GetSetMethod(nonPublic: false) is not null)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var srcProp in typeof(TRead).GetProperties())
        {
            if (filter is not null && !filter.Contains(srcProp.Name)) continue;
            if (excluded is not null && excluded.Contains(srcProp.Name)) continue;
            if (!targetProps.TryGetValue(srcProp.Name, out var tgtProp)) continue;
            if (!tgtProp.PropertyType.IsAssignableFrom(srcProp.PropertyType)) continue;
            if (srcProp.Name is nameof(IyuEntity.Id)
                or nameof(IyuEntity.CreatedAt)
                or nameof(IyuEntity.UpdatedAt))
                continue;
            tgtProp.SetValue(target, srcProp.GetValue(source));
        }
    }
}
