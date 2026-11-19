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
    /// Each board route also asks the host's <see cref="IBoardAccess"/> — who may view, make, edit, publish and delete which board is the application's to say (a board the caller may not view is 404, a thing they may not do to a board they can view is 403, and a list holds only viewable boards). <c>GET {prefix}/access</c> answers <c>{ create, assistant }</c>: whether the caller may make boards at all, and whether an <see cref="IBoardAssistant"/> is registered.
    /// </para>
    /// <para>
    /// With an <see cref="IBoardAssistant"/> registered, the routes also include <c>POST {prefix}/boards/{id}/turn</c>, one turn of the assistant's conversation about the board; with none the route is not mapped.
    /// </para>
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
        if (settings.BoardsEnabled)
        {
            // An assistant is on when the host registered one; the port is looked for by type, so nothing is built to find out.
            var assistant = endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IBoardAssistant));
            IyuUBoardBoardRoutes.Map(group, assistant);
            IyuUBoardBoardRoutes.MapAccess(group, assistant);
        }
        if (settings.CatalogEnabled) IyuUBoardCatalogRoute.Map(group);
        return group;
    }
}
