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
    public virtual async Task<IActionResult> Post([FromBody] TRead body, CancellationToken ct)
    {
        if (body is null) return BadRequest();
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var write = new TWrite();
        if (body.Id == Guid.Empty) body.Id = Guid.NewGuid();
        write.Id = body.Id;
        CopyCommonProperties(body, write);

        WriteSet.Add(write);
        await Context.SaveChangesAsync(ct);

        // Return the freshly materialized read row (includes server-assigned timestamps).
        var created = await ReadSet.AsNoTracking().FirstOrDefaultAsync(e => e.Id == write.Id, ct);
        return Created($"{Request.Path}({write.Id})", created ?? (object)write);
    }

    /// <summary>
    /// PATCH — partial update. Loads the existing write row, applies the delta,
    /// persists, and returns 204 (no body) on success or 404 if the key is unknown.
    /// </summary>
    public virtual async Task<IActionResult> Patch(Guid key, [FromBody] Delta<TRead> delta, CancellationToken ct)
    {
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
        CopySelectedProperties(readProjection, write, changedNames);
        await Context.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status204NoContent);
    }

    /// <summary>DELETE by key.</summary>
    public virtual async Task<IActionResult> Delete(Guid key, CancellationToken ct)
    {
        var write = await WriteSet.FirstOrDefaultAsync(e => e.Id == key, ct);
        if (write is null) return NotFound();

        WriteSet.Remove(write);
        await Context.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Copies overlapping (name + assignable type) properties from
    /// <paramref name="source"/> to <paramref name="target"/>. Used by POST
    /// where every field on the body is intentionally a new value. Nav
    /// properties and collections are naturally excluded because only scalar
    /// writable properties match.
    /// </summary>
    protected static void CopyCommonProperties(TRead source, TWrite target)
        => CopySelectedProperties(source, target, filter: null);

    /// <summary>
    /// Copies a subset of properties from <paramref name="source"/> to
    /// <paramref name="target"/>. When <paramref name="filter"/> is non-null,
    /// only property names present in it are considered. Always skips
    /// <c>Id</c>/<c>CreatedAt</c>/<c>UpdatedAt</c> — those are owned by the
    /// caller (Id) or the interceptor (timestamps).
    /// </summary>
    protected static void CopySelectedProperties(TRead source, TWrite target, ISet<string>? filter)
    {
        var targetProps = typeof(TWrite).GetProperties()
            .Where(p => p.CanWrite && p.GetSetMethod(nonPublic: false) is not null)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var srcProp in typeof(TRead).GetProperties())
        {
            if (filter is not null && !filter.Contains(srcProp.Name)) continue;
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
