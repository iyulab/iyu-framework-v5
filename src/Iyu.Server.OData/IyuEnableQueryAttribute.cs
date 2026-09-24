using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData;

namespace Iyu.Server.OData;

/// <summary>
/// <see cref="EnableQueryAttribute"/>, with the two refusals it makes itself answered in the same
/// shape as every other refusal of <see cref="IyuODataController{TRead, TWrite}"/>: an OData error
/// whose <c>error.code</c> is one of <see cref="ODataErrorCodes"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stock attribute answers a query option it cannot apply — an unknown property, a malformed
/// literal, a limit exceeded — with a <c>400</c> whose error has an empty code, and a key that names
/// no row with a <c>404</c> that has no body. It builds both in private members, so they are
/// relabelled here after it has run: <see cref="ODataErrorCodes.InvalidQuery"/> and
/// <see cref="ODataErrorCodes.KeyNotFound"/>. The message and, where detailed errors are enabled, the
/// <c>innererror</c> are the attribute's own.
/// </para>
/// <para>
/// Every option the stock attribute takes applies unchanged. A controller that overrides
/// <c>Get</c> keeps these answers by putting this attribute on the override.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public class IyuEnableQueryAttribute : EnableQueryAttribute
{
    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext actionExecutingContext)
    {
        base.OnActionExecuting(actionExecutingContext);
        actionExecutingContext.Result = Relabel(actionExecutingContext.Result);
    }

    /// <inheritdoc />
    public override void OnActionExecuted(ActionExecutedContext actionExecutedContext)
    {
        base.OnActionExecuted(actionExecutedContext);
        actionExecutedContext.Result = Relabel(actionExecutedContext.Result);
    }

    private static IActionResult? Relabel(IActionResult? result) => result switch
    {
        BadRequestObjectResult { Value: SerializableError serializable } => QueryRefusal(serializable.CreateODataError()),
        BadRequestObjectResult { Value: ODataError { Code: null or "" } error } => QueryRefusal(error),
        StatusCodeResult { StatusCode: StatusCodes.Status404NotFound } => KeyNotFound(),
        _ => result,
    };

    private static ObjectResult QueryRefusal(ODataError error)
    {
        error.Code = ODataErrorCodes.InvalidQuery;
        return new ObjectResult(error) { StatusCode = StatusCodes.Status400BadRequest };
    }

    /// <summary>The <c>404</c> every verb of the generic controller answers when the key names no row.</summary>
    internal static ObjectResult KeyNotFound()
        => new(new ODataError
        {
            Code = ODataErrorCodes.KeyNotFound,
            Message = "No entity in this set has the given key.",
        })
        { StatusCode = StatusCodes.Status404NotFound };
}
