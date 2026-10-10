using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>Maps the identity runtime's HTTP surface: anonymous token issuance and owner-scoped service-client management.</summary>
public static class IdentityEndpointRouteExtensions
{
    public static IEndpointRouteBuilder MapIyuIdentity(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        // Read by the handler rather than bound as a parameter: RFC 6749 clients send a form body or
        // a Basic header, and a record parameter binds only a JSON body.
        auth.MapPost("/token", (HttpContext http, IdentityTokenService tokens, CancellationToken ct)
            => IdentityEndpointHandlers.TokenAsync(http, tokens, ct)).AllowAnonymous();

        var clients = app.MapGroup("/api/service-clients")
            .RequireAuthorization(IyuIdentityServiceCollectionExtensions.CookiePolicyName);
        clients.MapPost("", (CreateServiceClientRequest req, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.CreateServiceClientAsync(req, owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        clients.MapGet("", (HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        clients.MapDelete("/{id:guid}", (Guid id, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.RevokeServiceClientAsync(id, owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        clients.MapPost("/{id:guid}/rotate", (Guid id, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.RotateServiceClientAsync(id, owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        clients.MapPatch("/{id:guid}/permissions", (Guid id, UpdateServiceClientPermissionsRequest req, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => TryOwnerId(http, out var owner)
                ? IdentityEndpointHandlers.UpdateServiceClientPermissionsAsync(id, req, owner, svc, ct)
                : Task.FromResult(Results.Unauthorized()));
        return app;
    }

    /// <summary>
    /// Maps sign-in through external providers (turned on by
    /// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuExternalSignIn"/>):
    /// <c>GET /api/auth/external/{provider}?returnUrl=…</c> sends the browser to the provider behind that handler, and
    /// <c>GET /api/auth/external/complete</c> — where the handler sends it back — opens the application's session for the
    /// account <see cref="IExternalLoginResolver"/> names. Both are anonymous: there is no session yet.
    /// </summary>
    public static IEndpointRouteBuilder MapIyuExternalSignIn(this IEndpointRouteBuilder app)
    {
        var external = app.MapGroup(IyuExternalSignInDefaults.StartPath).AllowAnonymous();
        external.MapGet("/complete", (string? returnUrl, HttpContext http, IExternalLoginResolver resolver,
                UserSessionService sessions, ILoggerFactory loggers)
            => ExternalSignInHandlers.CompleteAsync(returnUrl, http, resolver, sessions, loggers));
        external.MapGet("/{provider}", (string provider, string? returnUrl, HttpContext http,
                IAuthenticationSchemeProvider schemes)
            => ExternalSignInHandlers.ChallengeAsync(provider, returnUrl, http, schemes));
        return app;
    }

    private static bool TryOwnerId(HttpContext http, out Guid id) =>
        Guid.TryParse(IyuIdentityClaims.FindSubject(http.User), out id);
}
