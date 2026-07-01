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

        var clients = app.MapGroup("/api/service-clients")
            .RequireAuthorization(IdentityServiceCollectionExtensions.CookiePolicyName);
        clients.MapPost("", (CreateServiceClientRequest req, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.CreateServiceClientAsync(req, owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        clients.MapDelete("/{id:guid}", (Guid id, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.RevokeServiceClientAsync(id, owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        clients.MapPost("/{id:guid}/rotate", (Guid id, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.RotateServiceClientAsync(id, owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        return app;
    }

    private static bool TryOwnerId(HttpContext http, out Guid id) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out id);
}
