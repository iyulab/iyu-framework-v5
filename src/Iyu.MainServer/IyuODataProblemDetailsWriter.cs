using System.Diagnostics;
using System.Text.Json;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iyu.MainServer;

/// <summary>
/// Writes a <c>ProblemDetails</c> answered on the OData route as an OData error object, so a request
/// under that route gets one error format whatever produced the failure.
/// </summary>
/// <remarks>
/// <para>
/// The exception handlers decide <i>what</i> a failure is — its status, its wording, optionally its
/// code. This writer decides only the <i>format</i>, at the point the body is written, which is why a
/// handler a consumer registers (one that classifies its provider's write failures, say) keeps working
/// unchanged and simply comes out in the route's format. A failure answered with no body on the route
/// (a policy's <c>401</c>/<c>403</c>, an unmatched path's <c>404</c>) reaches it through the status-code
/// pages <c>UseIyuMainServer</c> enables for the route.
/// </para>
/// <para>
/// <c>error.code</c> is the <c>code</c> extension when the problem carries a string one; otherwise the
/// status's reason phrase without spaces — <see cref="ODataErrorCodes.Conflict"/> for a <c>409</c>,
/// <see cref="ODataErrorCodes.InternalServerError"/> for a <c>500</c>. <c>error.message</c> is the
/// problem's detail, else its title, else the reason phrase. <c>error.innererror.traceId</c> names the
/// request in the server's logs; nothing about the exception is written.
/// </para>
/// </remarks>
internal sealed class IyuODataProblemDetailsWriter(string routePrefix) : IProblemDetailsWriter
{
    private readonly string _routePrefix = routePrefix;

    public bool CanWrite(ProblemDetailsContext context) => IsUnderRoute(context.HttpContext.Request, _routePrefix);

    /// <summary>Whether <paramref name="request"/> is under the OData route — the whole application when the prefix is empty.</summary>
    internal static bool IsUnderRoute(HttpRequest request, string routePrefix)
        => string.IsNullOrEmpty(routePrefix) || request.Path.StartsWithSegments("/" + routePrefix);

    public async ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var http = context.HttpContext;
        var problem = context.ProblemDetails;
        var status = problem.Status ?? http.Response.StatusCode;
        problem.Status = status;
        http.RequestServices.GetService<IOptions<ProblemDetailsOptions>>()?.Value.CustomizeProblemDetails?.Invoke(context);

        var reason = ReasonPhrases.GetReasonPhrase(status);
        var code = problem.Extensions.TryGetValue("code", out var supplied) && supplied is string s && s.Length > 0
            ? s
            : reason.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        var message = problem.Detail ?? problem.Title ?? reason;
        var traceId = Activity.Current?.Id ?? http.TraceIdentifier;

        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; odata.metadata=minimal; odata.streaming=true; charset=utf-8";
        http.Response.Headers["OData-Version"] = "4.0";

        await using var json = new Utf8JsonWriter(http.Response.Body);
        json.WriteStartObject();
        json.WriteStartObject("error");
        json.WriteString("code", code);
        json.WriteString("message", message);
        json.WriteStartObject("innererror");
        json.WriteString("traceId", traceId);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(http.RequestAborted);
    }
}
