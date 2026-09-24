using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData;

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
    /// are applied by <see cref="IyuEnableQueryAttribute"/>.
    /// </summary>
    [IyuEnableQuery]
    public virtual IQueryable<TRead> Get() => ReadSet.AsNoTracking();

    /// <summary>GET by key — returns a single entity, or 404 when no row has the key.</summary>
    /// <remarks>
    /// Returned as a <see cref="SingleResult{T}"/> over the query rather than a materialized
    /// entity, so the query attribute composes <c>$expand</c> and <c>$select</c> into the
    /// database query the same way it does for the collection <see cref="Get()"/>. A materialized
    /// entity has no navigation loaded, and expanding it answers a collection as empty and a
    /// reference as absent — a valid-looking response that is simply wrong. The 404 for a missing
    /// key comes from <see cref="IyuEnableQueryAttribute"/>, which answers an empty single result that way.
    /// </remarks>
    [IyuEnableQuery]
    public virtual SingleResult<TRead> Get(Guid key)
        => SingleResult.Create(ReadSet.AsNoTracking().Where(e => e.Id == key));

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
        // Checked before the null check below: a malformed EDM literal (e.g. a DateTimeOffset
        // string with no offset) fails the whole [FromBody] bind — body comes back null with no
        // clue why, unless the binder's own ModelState entry is looked at first. See
        // SanitizedModelState's remarks for why that entry needs sanitizing before it goes out.
        if (!ModelState.IsValid) return Invalid(SanitizedModelState(), ODataErrorCodes.InvalidBody);
        if (body is null) return MissingBody();

        var pair = registry.FindByReadType(typeof(TRead));
        if (pair?.SharedKeyPrincipalSet is not null
            && await SharedKeyRejectionAsync(registry, pair, body.Id, ct) is { } refused)
            return refused;

        var write = new TWrite();
        if (body.Id == Guid.Empty) body.Id = Guid.NewGuid();
        write.Id = body.Id;
        CopyCommonProperties(body, write, pair?.WriteExcludedProperties);

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
        // Same reasoning as Post: a malformed EDM literal fails the whole Delta<TRead> bind before
        // NotFound is even checked, and the binder's own ModelState entry needs sanitizing — see
        // SanitizedModelState's remarks.
        if (!ModelState.IsValid) return Invalid(SanitizedModelState(), ODataErrorCodes.InvalidBody);
        if (delta is null) return MissingBody();

        var write = await WriteSet.FirstOrDefaultAsync(e => e.Id == key, ct);
        if (write is null) return IyuEnableQueryAttribute.KeyNotFound();

        // Apply ONLY the properties the client actually set. Copying the full
        // TRead placeholder would overwrite untouched fields with defaults.
        var changedNames = delta.GetChangedPropertyNames().ToHashSet(StringComparer.Ordinal);
        if (changedNames.Count == 0)
            return StatusCode(StatusCodes.Status204NoContent);

        var readProjection = Activator.CreateInstance<TRead>();
        delta.Patch(readProjection);

        if (!ValidateChangedProperties(readProjection, changedNames))
            return Invalid(SanitizedModelState(), ODataErrorCodes.InvalidBody);

        var excludedFromWrite = registry.FindByReadType(typeof(TRead))?.WriteExcludedProperties;
        var (writable, unwritable) = PartitionByWritability(changedNames, excludedFromWrite);

        // A request whose every property is unwritable cannot change anything. Reporting
        // success for it is what let a caller conclude the field was locked rather than
        // absent from the write model.
        if (writable.Count == 0)
            return Invalid(UnwritablePropertiesModelState(unwritable), ODataErrorCodes.UnwritableProperty);

        CopySelectedProperties(readProjection, write, writable, excludedFromWrite);
        await Context.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// Splits the sent property names into the ones this update can actually store
    /// and the ones it cannot, each of the latter with the reason it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the single place that decides writability.</b> The same set is used
    /// to answer the caller and to drive <see cref="CopySelectedProperties"/>, so the
    /// two cannot disagree. A second predicate that mirrored the copy's skip rules
    /// would drift the first time a fourth rule is added.
    /// </para>
    /// <para>
    /// A property the read type declares but the write type does not is the ordinary
    /// derived column — a view produces it, no table stores it. Sending it back is
    /// normal and stays harmless: it is dropped whenever anything else in the same
    /// request is writable, which is what keeps a whole-object round-trip working.
    /// </para>
    /// </remarks>
    private static (ISet<string> Writable, IReadOnlyDictionary<string, string> Unwritable) PartitionByWritability(
        ISet<string> changedNames, IReadOnlySet<string>? excludedFromWrite)
    {
        var targetProps = typeof(TWrite).GetProperties()
            .Where(p => p.CanWrite && p.GetSetMethod(nonPublic: false) is not null)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
        var sourceProps = typeof(TRead).GetProperties()
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        var writable = new HashSet<string>(StringComparer.Ordinal);
        var unwritable = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in changedNames)
        {
            if (name is nameof(IyuEntity.Id) or nameof(IyuEntity.CreatedAt) or nameof(IyuEntity.UpdatedAt))
            {
                unwritable[name] = "The property is managed by the server and cannot be updated.";
                continue;
            }
            if (excludedFromWrite is not null && excludedFromWrite.Contains(name))
            {
                unwritable[name] = "The property is read-only: the model excludes it from writes.";
                continue;
            }
            if (!sourceProps.TryGetValue(name, out var srcProp)
                || !targetProps.TryGetValue(name, out var tgtProp))
            {
                unwritable[name] = "The property is read-only: it is derived and has no stored counterpart.";
                continue;
            }
            if (!tgtProp.PropertyType.IsAssignableFrom(srcProp.PropertyType))
            {
                unwritable[name] = "The property cannot be stored: its type has no writable counterpart.";
                continue;
            }
            writable.Add(name);
        }

        return (writable, unwritable);
    }

    /// <summary>
    /// Builds the 400 body for an update that could not have stored anything, keyed by
    /// property so a caller handles it exactly like a validation failure.
    /// </summary>
    /// <remarks>
    /// Echoing the property names carries nothing the caller did not already write. What
    /// it adds is the distinction they could not otherwise draw: a value refused because
    /// it is derived, versus one refused because a policy locked it.
    /// </remarks>
    private static ModelStateDictionary UnwritablePropertiesModelState(IReadOnlyDictionary<string, string> unwritable)
    {
        var state = new ModelStateDictionary();
        foreach (var (name, reason) in unwritable) state.AddModelError(name, reason);
        return state;
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
        if (write is null) return IyuEnableQueryAttribute.KeyNotFound();

        WriteSet.Remove(write);
        await Context.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// The three ways a POST to a shared-key set can fail before anything is written, or
    /// <see langword="null"/> when none of them applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only reached for a set declared through
    /// <see cref="IyuEntityPairRegistry.DeclareSharedKey"/>. An ordinary set keeps the behaviour
    /// it has always had, including a server-invented key — that behaviour is correct wherever
    /// the key is the row's own.
    /// </para>
    /// <para>
    /// The principal is looked up on its <em>write</em> type. The key is a reference to the
    /// principal's stored row, and the read side may be a view that filters, projects, or lags
    /// behind it; answering "does the principal exist" from a view would make the answer depend on
    /// what that view chooses to show.
    /// </para>
    /// <para>
    /// This is a check, not a constraint. Under a relational provider the foreign key enforces the
    /// same relationship at <c>SaveChanges</c> time and would refuse the write regardless; what
    /// this adds is a stated status and reason in place of whatever a constraint violation would
    /// surface as. Both layers are wanted — a check alone races, and a constraint alone explains
    /// nothing.
    /// </para>
    /// </remarks>
    private async Task<IActionResult?> SharedKeyRejectionAsync(
        IyuEntityPairRegistry registry,
        IyuEntityPairRegistry.EntityPair pair,
        Guid key,
        CancellationToken ct)
    {
        var principalSet = pair.SharedKeyPrincipalSet!;

        if (key == Guid.Empty)
        {
            var state = new ModelStateDictionary();
            state.AddModelError(
                nameof(IyuEntity.Id),
                $"The key is required: entity set '{pair.SetName}' shares its key with "
                + $"'{principalSet}', so the key identifies which row of '{principalSet}' this "
                + "belongs to and cannot be assigned by the server.");
            return Invalid(state, ODataErrorCodes.SharedKeyRequired);
        }

        if (registry.Find(principalSet) is not { } principal)
            throw new InvalidOperationException(
                $"Entity set '{pair.SetName}' is declared to share its key with '{principalSet}', "
                + "which is not registered.");

        var owner = await Context.FindAsync(principal.WriteType, [key], ct);
        if (owner is not null) Context.Entry(owner).State = EntityState.Detached;
        if (owner is null)
            return Refusal(StatusCodes.Status409Conflict, ODataErrorCodes.SharedKeyPrincipalMissing,
                $"No row of entity set '{principalSet}' has key {key}, so there is nothing for "
                + $"this '{pair.SetName}' row to belong to.");

        var existing = await WriteSet.AsNoTracking().FirstOrDefaultAsync(e => e.Id == key, ct);
        return existing is null
            ? null
            : Refusal(StatusCodes.Status409Conflict, ODataErrorCodes.SharedKeyRowExists,
                $"Entity set '{pair.SetName}' already has the row with key {key}: a set that "
                + $"shares its key with '{principalSet}' holds at most one row per principal.");
    }

    /// <summary>
    /// A refusal in the OData error shape — <c>{"error":{"code","message"}}</c> — with a code from
    /// <see cref="ODataErrorCodes"/>. Every refusal this controller makes goes through here or
    /// <see cref="Invalid"/>, so a caller parses one shape and branches on one field.
    /// </summary>
    private static ObjectResult Refusal(int status, string code, string message)
        => new(new ODataError { Code = code, Message = message }) { StatusCode = status };

    /// <summary>A <c>400</c> for a request that carried no body to bind.</summary>
    private static ObjectResult MissingBody()
        => Refusal(StatusCodes.Status400BadRequest, ODataErrorCodes.InvalidBody, "The request has no body.");

    /// <summary>
    /// A <c>400</c> carrying the per-property messages of <paramref name="state"/> as the error's
    /// <c>details</c>, each with its <c>target</c> — the shape OData gives a model-state
    /// <c>400</c> already, with <paramref name="code"/> in place of the empty code it leaves.
    /// </summary>
    private static ObjectResult Invalid(ModelStateDictionary state, string code)
    {
        var error = new SerializableError(state).CreateODataError();
        error.Code = code;
        return new ObjectResult(error) { StatusCode = StatusCodes.Status400BadRequest };
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

        return Refusal(StatusCodes.Status405MethodNotAllowed, ODataErrorCodes.ReadOnlySet,
            $"Entity set '{pair.SetName}' is registered read-only for {verb} and does not accept this request.");
    }

    /// <summary>
    /// Rebuilds <c>ModelState</c> with every binder/deserialization-originated error
    /// message replaced by a generic one, before it is returned to the client.
    /// </summary>
    /// <remarks>
    /// A malformed EDM literal (e.g. a <c>DateTimeOffset</c> string with no offset) fails OData's
    /// own body binder, which adds an error to <c>ModelState</c> carrying the raw
    /// <c>ODataException</c> message — internal vocabulary (<c>Edm.DateTimeOffset</c>, the
    /// exception's type name) a client has no business seeing. A <c>[Required]</c>-style
    /// annotation failure, by contrast, never attaches an <see cref="ModelError.Exception"/> — MVC
    /// only sets it for errors raised by model binding/deserialization, never for
    /// <c>TryValidateModel</c>'s own DataAnnotations messages. That is the distinction this method
    /// relies on, not string-matching the message text: it survives a future OData/EF package
    /// upgrade changing exactly what that text says, which is not something a regex could promise.
    /// </remarks>
    private ModelStateDictionary SanitizedModelState()
    {
        var sanitized = new ModelStateDictionary();
        foreach (var (key, entry) in ModelState)
        {
            if (entry is null) continue;
            foreach (var error in entry.Errors)
            {
                sanitized.AddModelError(key, error.Exception is not null
                    ? "The value could not be converted to its expected type."
                    : error.ErrorMessage);
            }
        }
        return sanitized;
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
