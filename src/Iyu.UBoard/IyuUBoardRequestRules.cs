using Microsoft.AspNetCore.Http;

namespace Iyu.UBoard;

/// <summary>The rules every route of the layer shares about where a request may come from and what it may carry.</summary>
internal static class IyuUBoardRequestRules
{
    /// <summary>
    /// Browsers say where a request came from; anything but this origin is another site acting with the caller's cookie, so a
    /// <c>Sec-Fetch-Site</c> other than <c>same-origin</c> (or <c>none</c>, a typed address), or an <c>Origin</c> that is not this
    /// host, is refused with 403 — except a person arriving at a page of the screen from another site (see <see cref="IsPageNavigation"/>).
    /// A request that carries neither header is let through: browsers send <c>Sec-Fetch-*</c> only over HTTPS or to localhost, and a client
    /// that is not a browser sends neither but holds the caller's credentials already, so refusing it would break the callers the routes are for.
    /// </summary>
    internal static ValueTask<object?> SameOrigin(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var request = invocation.HttpContext.Request;
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none") && !IsPageNavigation(invocation.HttpContext))
            return new(Results.StatusCode(StatusCodes.Status403Forbidden));
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
            return new(Results.StatusCode(StatusCodes.Status403Forbidden));

        return next(invocation);
    }

    /// <summary>
    /// A person arriving at a page of the screen from another site — a link in a mail, or the identity provider sending them back after sign-in — is
    /// a top-level navigation the browser marks <c>Sec-Fetch-Site: cross-site</c>. Only that, only for a page route (<see cref="ScreenPageMetadata"/>),
    /// only a GET that is a navigation to a document (so not a frame, a script or a fetch), is let through: the page has no state to change, it is
    /// framed by nobody (its policy), and what it asks of the host afterwards is same-origin like every other request.
    /// </summary>
    private static bool IsPageNavigation(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method)
        && http.Request.Headers["Sec-Fetch-Mode"].ToString() == "navigate"
        && http.Request.Headers["Sec-Fetch-Dest"].ToString() == "document"
        && http.GetEndpoint()?.Metadata.GetMetadata<ScreenPageMetadata>() is not null;

    /// <summary>
    /// Keeps what the routes answer out of any cache that is not the caller's own: board documents, revisions and the catalog are the caller's
    /// view of data they may read, and a cache in front of the host that keys on the path would hand it to someone else. A route that
    /// sets its own policy (the screen's files, which are the same for everyone who may open the screen) keeps it.
    /// </summary>
    internal static ValueTask<object?> NoStoreUnlessSet(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var response = invocation.HttpContext.Response;
        response.OnStarting(() =>
        {
            if (!response.Headers.ContainsKey("Cache-Control")) response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next(invocation);
    }

    /// <summary>A form post (a request another site can send without asking) is not JSON: a request that carries a body must say it is.</summary>
    internal static ValueTask<object?> RequireJson(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) =>
        invocation.HttpContext.Request.HasJsonContentType()
            ? next(invocation)
            : new(Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));

    /// <summary>Like <see cref="RequireJson"/> for a route that may be called without a body: only a request that has one is held to it.</summary>
    internal static ValueTask<object?> RequireJsonWhenThereIsABody(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var request = invocation.HttpContext.Request;
        var hasBody = request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding");
        return hasBody ? RequireJson(invocation, next) : next(invocation);
    }
}
