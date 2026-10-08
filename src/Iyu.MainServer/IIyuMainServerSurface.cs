using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer;

/// <summary>
/// A data surface a host composes in beside the built-in OData one — registered on
/// <see cref="IyuMainServerOptions"/> through <see cref="IyuMainServerOptions.Surface{TSurface}"/>, then wired by
/// <c>AddIyuMainServer</c> and mapped by <c>UseIyuMainServer</c>.
/// </summary>
/// <remarks>
/// This is what keeps an optional surface optional at the package level: <c>Iyu.MainServer</c> does not reference
/// it, so a host that does not use it carries none of its dependencies. The GraphQL surface is one —
/// <c>Iyu.MainServer.GraphQL</c>.
/// </remarks>
public interface IIyuMainServerSurface
{
    /// <summary>
    /// Registers the surface's services — called once by <c>AddIyuMainServer</c>, after the registration
    /// callback has run, so every entity pair is known. A surface with nothing registered on it registers nothing.
    /// It registers its own <c>IAuthorizationSurfaceProvider</c> so the authorization surface report covers it.
    /// </summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>Maps the surface's endpoints — called once by <c>UseIyuMainServer</c>.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
