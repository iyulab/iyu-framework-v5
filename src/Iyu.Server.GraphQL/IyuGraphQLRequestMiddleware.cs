using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Server.GraphQL;

/// <summary>
/// The two things this surface does with a request's document once the executor has parsed it and before it validates or
/// runs it: refuse one too large to read (<see cref="IyuGraphQLDocumentBudget"/>), and put the query fields it reaches on
/// <c>HttpContext.Features</c> (<see cref="IIyuGraphQLReachFeature"/>). Added by
/// <see cref="IyuGraphQLSchemaBuilder.ApplyTo"/> — the budget always, the reach when
/// <see cref="IyuGraphQLSchemaBuilder.ComputeReach"/> is on or any query field carries a policy.
/// </summary>
internal static class IyuGraphQLRequestMiddleware
{
    internal const string Key = "IyuGraphQLRequest";

    /// <summary>The error code of a document refused for its size.</summary>
    internal const string DocumentTooLargeCode = "IYU_DOCUMENT_TOO_LARGE";

    internal static ValueTask InvokeAsync(
        IyuGraphQLSchemaBuilder builder, HotChocolate.Execution.RequestDelegate next, RequestContext context)
    {
        if (context.OperationDocumentInfo.Document is { } document)
        {
            if (IyuGraphQLDocumentBudget.Exceeds(document, builder.MaxDocumentSelections) is { } reason)
            {
                // Nothing further runs: validating the document is what it would have cost.
                context.Result = OperationResult.FromError(
                    ErrorBuilder.New()
                        .SetMessage($"The document {reason}.")
                        .SetCode(DocumentTooLargeCode)
                        .Build());
                return ValueTask.CompletedTask;
            }

            if (builder.ReachIsComputed) Record(builder, context, document);
        }
        return next(context);
    }

    private static void Record(IyuGraphQLSchemaBuilder builder, RequestContext context, DocumentNode document)
    {
        // Not an HTTP request (an executor called directly): there is no request to record the reach on.
        var http = context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http is null) return;

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
