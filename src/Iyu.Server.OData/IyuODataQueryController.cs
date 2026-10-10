using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Abstracts;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData;

namespace Iyu.Server.OData;

/// <summary>
/// The read half of every generic OData controller: GET and GET(key) over the read type's set.
/// </summary>
/// <remarks>
/// Not a base to derive from directly — a set is either a read/write pair
/// (<see cref="IyuODataController{TRead,TWrite}"/>) or read-only (<see cref="IyuODataReadOnlyController{TRead}"/>),
/// and both read through here so a query option, the ETag header and a missing key answer the same way on each.
/// </remarks>
public abstract class IyuODataQueryController<TRead> : ODataController
    where TRead : class, IKeyed
{
    /// <summary>The EF Core context the controller reads (and, for a pair, writes) through.</summary>
    protected IyuDbContext Context { get; }

    /// <summary>DbSet backing the read (view) type.</summary>
    protected DbSet<TRead> ReadSet => Context.Set<TRead>();

    private protected IyuODataQueryController(IyuDbContext context)
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
    // Declared first so that it runs last on the way out — after the query attribute has turned the
    // SingleResult into the one entity the header is computed from.
    [ETagActionFilter]
    [IyuEnableQuery]
    public virtual SingleResult<TRead> Get(Guid key)
        => SingleResult.Create(ReadSet.AsNoTracking().Where(e => e.Id == key));

    /// <summary>
    /// A refusal in the OData error shape — <c>{"error":{"code","message"}}</c> — with a code from
    /// <see cref="ODataErrorCodes"/>, so a caller parses one shape and branches on one field.
    /// </summary>
    private protected static ObjectResult Refusal(int status, string code, string message)
        => new(new ODataError { Code = code, Message = message }) { StatusCode = status };
}

/// <summary>
/// Generic OData controller for a set that is only read — a model with no table of its own to write to, such as a
/// view over other sets. GET and GET(key) behave as on any set; POST, PATCH and DELETE are refused.
/// </summary>
/// <remarks>
/// <para>
/// Subclass it with the read type: <c>public class OrderSummariesController : IyuODataReadOnlyController&lt;OrderSummary&gt;</c>,
/// and register the set with <see cref="IyuEdmModelBuilder.AddReadOnlySet{TRead}"/>. The read type needs a key and
/// nothing else (<see cref="IKeyed"/>) — no write type stands behind it, and it inherits no audit timestamps.
/// </para>
/// <para>
/// The write verbs are declared here rather than left unrouted so that they answer the way a read-only pair does —
/// <c>405</c> with <see cref="ODataErrorCodes.ReadOnlySet"/> — instead of a bare method-not-allowed with no body: a
/// client handles one refusal for "this set is not written through here", whichever way the set came to be read-only.
/// </para>
/// </remarks>
public abstract class IyuODataReadOnlyController<TRead> : IyuODataQueryController<TRead>
    where TRead : class, IKeyed
{
    protected IyuODataReadOnlyController(IyuDbContext context) : base(context)
    {
    }

    /// <summary>POST — refused: the set is read-only.</summary>
    public IActionResult Post([FromServices] IyuEntityPairRegistry registry) => ReadOnly(registry, ODataVerb.Post);

    /// <summary>PATCH — refused: the set is read-only.</summary>
    public IActionResult Patch(Guid key, [FromServices] IyuEntityPairRegistry registry) => ReadOnly(registry, ODataVerb.Patch);

    /// <summary>DELETE — refused: the set is read-only.</summary>
    public IActionResult Delete(Guid key, [FromServices] IyuEntityPairRegistry registry) => ReadOnly(registry, ODataVerb.Delete);

    private static ObjectResult ReadOnly(IyuEntityPairRegistry registry, ODataVerb verb)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var setName = registry.FindByReadType(typeof(TRead))?.SetName ?? typeof(TRead).Name;
        return Refusal(StatusCodes.Status405MethodNotAllowed, ODataErrorCodes.ReadOnlySet,
            $"Entity set '{setName}' is registered read-only for {verb} and does not accept this request.");
    }
}
