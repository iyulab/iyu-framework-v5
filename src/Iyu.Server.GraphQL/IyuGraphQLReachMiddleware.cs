using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Puts the query fields a GraphQL request reaches on <c>HttpContext.Features</c> once the executor has the request's
/// document and before it validates or runs it (<see cref="IIyuGraphQLReachFeature"/>). Added by
/// <see cref="IyuGraphQLSchemaBuilder.ApplyTo"/> when <see cref="IyuGraphQLSchemaBuilder.ComputeReach"/> is on or any
/// query field carries a policy.
/// </summary>
internal static class IyuGraphQLReachMiddleware
{
    internal const string Key = "IyuGraphQLReach";

    internal static ValueTask InvokeAsync(IyuGraphQLSchemaBuilder builder, HotChocolate.Execution.RequestDelegate next, RequestContext context)
    {
        Record(builder, context);
        return next(context);
    }

    private static void Record(IyuGraphQLSchemaBuilder builder, RequestContext context)
    {
        // Not an HTTP request (an executor called directly): there is no request to record the reach on.
        var http = context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http is null || context.OperationDocumentInfo.Document is not { } document) return;

        var reach = IyuGraphQLReach.Of(
            context.Schema, document, context.Request.OperationName, builder.QueryNames, builder.QueryNameOf);
        if (reach is not var (addressed, reached)) return;

        // A batch sends several operations through one HTTP request: the record is their union.
        if (http.Features.Get<IIyuGraphQLReachFeature>() is { } earlier)
        {
            addressed = earlier.AddressedQueries.Union(addressed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            reached = earlier.ReachedQueries.Union(reached, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        }
        http.Features.Set<IIyuGraphQLReachFeature>(new IyuGraphQLReachFeature(addressed, reached));
    }
}
