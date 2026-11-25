using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Iyu.UBoard;

/// <summary>The source catalog route: <c>GET {prefix}/catalog</c>, what the signed-in caller may build a board from.</summary>
internal static class IyuUBoardCatalogRoute
{
    private static readonly JsonSerializerOptions Wire = IyuUBoardWire.OmittingNulls;

    internal static void Map(RouteGroupBuilder group) =>
        group.MapGet("catalog", async (ClaimsPrincipal user, IBoardSourceCatalog catalog, IOptions<IyuUBoardOptions> options, HttpContext http) =>
            Results.Json(await catalog.DescribeAsync(user, options.Value.ShowUnreadable, http.RequestAborted), Wire));
}

/// <summary>Fails the host's start when the catalog was turned on and nothing was registered to describe the sources.</summary>
internal sealed class IyuUBoardCatalogCheck : IHostedService
{
    public IyuUBoardCatalogCheck(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        if (scope.ServiceProvider.GetService<IBoardSourceCatalog>() is not null) return;

        throw new InvalidOperationException(
            "AddIyuUBoard with Catalog() needs the host to register an IBoardSourceCatalog (Iyu.UBoard.MainServer describes the sources of an Iyu.MainServer host).");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
