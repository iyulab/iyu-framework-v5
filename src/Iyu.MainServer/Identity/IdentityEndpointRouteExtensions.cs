using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iyu.MainServer.Identity;

/// <summary>Maps the identity runtime's HTTP surface: anonymous token issuance and owner-scoped service-client management.</summary>
public static class IdentityEndpointRouteExtensions
{
    public static IEndpointRouteBuilder MapIyuIdentity(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/token", (TokenRequest req, IdentityTokenService tokens, CancellationToken ct)
            => IdentityEndpointHandlers.TokenAsync(req, tokens, ct)).AllowAnonymous();

        var clients = app.MapGroup("/api/service-clients").RequireAuthorization();
        clients.MapPost("", (CreateServiceClientRequest req, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => IdentityEndpointHandlers.CreateServiceClientAsync(req, OwnerId(http), svc, ct));
        clients.MapDelete("/{id:guid}", (Guid id, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => IdentityEndpointHandlers.RevokeServiceClientAsync(id, OwnerId(http), svc, ct));
        return app;
    }

    private static Guid OwnerId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
