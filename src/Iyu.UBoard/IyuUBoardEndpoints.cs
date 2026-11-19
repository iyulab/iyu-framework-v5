using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.UBoard;

/// <summary>Puts the parts of the board layer behind routes.</summary>
public static class IyuUBoardEndpoints
{
    /// <summary>
    /// Maps the parts that were turned on: with <see cref="IyuUBoardOptions.Boards"/>, the board routes under <c>{prefix}/boards</c> (make, read, save by revision, rename,
    /// delete, revisions, publish, withdraw, publications, the published revision, the fallback list and a paged list).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The routes require a signed-in caller (or <see cref="IyuUBoardOptions.Policy"/>); there is no anonymous mode. A request
    /// must be same-origin: a browser's <c>Sec-Fetch-Site</c> other than <c>same-origin</c>, or an <c>Origin</c> that is not this
    /// host, is refused with 403, so a page on another site cannot act with the caller's cookie.
    /// </para>
    /// </remarks>
    public static RouteGroupBuilder MapIyuUBoard(this IEndpointRouteBuilder endpoints, string prefix = "/uboard")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<IyuUBoardOptions>>().Value;

        var group = endpoints.MapGroup(prefix);
        if (settings.Policy is { Length: > 0 } policy) group.RequireAuthorization(policy);
        else group.RequireAuthorization();
        group.AddEndpointFilter(IyuUBoardRequestRules.SameOrigin);
        if (settings.BoardsEnabled) IyuUBoardBoardRoutes.Map(group);
        if (settings.CatalogEnabled) IyuUBoardCatalogRoute.Map(group);
        return group;
    }
}
