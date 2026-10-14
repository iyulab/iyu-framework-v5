using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>Maps the identity runtime's HTTP surface: anonymous token issuance and owner-scoped service-client management.</summary>
public static class IdentityEndpointRouteExtensions
{
    /// <summary>
    /// Maps <c>POST /api/auth/token</c>, which answers the grants the application turned on —
    /// <c>client_credentials</c> with <see cref="IyuIdentityServiceCollectionExtensions.AddIyuServiceClients"/>,
    /// <c>refresh_token</c> with <see cref="IyuIdentityServiceCollectionExtensions.AddIyuRefreshTokens"/>, any other with
    /// <c>unsupported_grant_type</c> — and, with service clients on, the owner's management endpoints under
    /// <c>/api/service-clients</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapIyuIdentity(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        // Read by the handler rather than bound as a parameter: RFC 6749 clients send a form body or
        // a Basic header, and a record parameter binds only a JSON body.
        auth.MapPost("/token", (HttpContext http, CancellationToken ct)
            => IdentityEndpointHandlers.TokenAsync(http, ct)).AllowAnonymous();

        if (!app.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(ServiceClientService)))
            return app;

        var clients = app.MapGroup("/api/service-clients")
            .RequireAuthorization(IyuIdentityServiceCollectionExtensions.CookiePolicyName);
        clients.MapPost("", async (CreateServiceClientRequest req, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => await OwnerAsync(http) is { } owner
                ? await IdentityEndpointHandlers.CreateServiceClientAsync(req, owner, svc, ct)
                : Results.Unauthorized());
        clients.MapGet("", async (HttpContext http, ServiceClientService svc, CancellationToken ct)
            => await OwnerAsync(http) is { } owner
                ? await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, ct)
                : Results.Unauthorized());
        clients.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => await OwnerAsync(http) is { } owner
                ? await IdentityEndpointHandlers.RevokeServiceClientAsync(id, owner, svc, ct)
                : Results.Unauthorized());
        clients.MapPost("/{id:guid}/rotate", async (Guid id, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => await OwnerAsync(http) is { } owner
                ? await IdentityEndpointHandlers.RotateServiceClientAsync(id, owner, svc, ct)
                : Results.Unauthorized());
        clients.MapPatch("/{id:guid}/permissions", async (Guid id, UpdateServiceClientPermissionsRequest req, HttpContext http, ServiceClientService svc, CancellationToken ct)
            => await OwnerAsync(http) is { } owner
                ? await IdentityEndpointHandlers.UpdateServiceClientPermissionsAsync(id, req, owner, svc, ct)
                : Results.Unauthorized());
        return app;
    }

    /// <summary>
    /// Maps an administrator's view of every owner's service clients under <c>/api/admin/service-clients</c>:
    /// <c>GET</c> lists them (<c>?owner={id}</c> narrows to one owner, <c>?issuer={id}</c> to what one account issued),
    /// <c>DELETE /{id}</c> revokes one whoever owns it, and <c>DELETE /owner/{ownerId}</c> revokes every active client of
    /// one owner, answering how many. Where the host names its service accounts (<see cref="IServiceAccounts"/>), an
    /// administrator also acts on a service account's behalf: <c>POST</c> issues a client held under one
    /// (<c>{ ownerUserId, displayName, permissions, expiresAt }</c> — within the service account's permissions, not the
    /// administrator's), <c>POST /{id}/rotate</c> and <c>PATCH /{id}/permissions</c> manage one; a person's account is
    /// refused with <c>403 owner_not_service_account</c>.
    /// </summary>
    /// <param name="app">The endpoint builder.</param>
    /// <param name="policy">
    /// The authorization policy an administrator meets — the application's to name (a permission code from its
    /// catalog, say); the framework knows no administrator. Like the owner's endpoints, these also require a session
    /// cookie, and the policy is met by the person whose session it is: a bearer token never manages service clients,
    /// whatever the policy accepts — alone, or presented beside someone's session cookie.
    /// </param>
    /// <remarks>
    /// The step after a password reset: the reset and a signed-out session do not end the credentials the account
    /// issued, and the owner is the one person who cannot be asked to find them. Revoked clients are listed, marked.
    /// Requires <see cref="IyuIdentityServiceCollectionExtensions.AddIyuServiceClients"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Service clients are not turned on.</exception>
    public static RouteGroupBuilder MapIyuServiceClientAdministration(this IEndpointRouteBuilder app, string policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        if (!app.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(ServiceClientService)))
            throw new InvalidOperationException(
                "Service-client administration needs service clients turned on: call services.AddIyuServiceClients().");

        // The policy is met by the person whose session cookie it is — not by a bearer token presented beside it. Both
        // schemes are asked, as a permission's policy asks them, so a caller with only a bearer token is answered 403.
        var admin = app.MapGroup("/api/admin/service-clients")
            .RequireAuthorization(IyuIdentityServiceCollectionExtensions.CookiePolicyName)
            .RequireAuthorization(p => p
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme)
                .AddRequirements(new SessionCookiePolicyRequirement(policy)));
        admin.MapGet("", async (Guid? owner, Guid? issuer, ServiceClientService svc, CancellationToken ct) =>
        {
            var clients = owner is { } id ? await svc.ListAsync(id, ct) : await svc.ListAllAsync(ct);
            return Results.Ok(issuer is { } by ? clients.Where(c => c.IssuedBy == by).ToList() : clients);
        });
        admin.MapDelete("/{id:guid}", async (Guid id, ServiceClientService svc, CancellationToken ct)
            => await svc.RevokeAnyAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        admin.MapDelete("/owner/{ownerId:guid}", async (Guid ownerId, ServiceClientService svc, CancellationToken ct)
            => Results.Ok(new { revoked = await svc.RevokeAllOfOwnerAsync(ownerId, ct) }));

        // On a service account's behalf — only where the host says which accounts those are (IServiceAccounts).
        if (app.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IServiceAccounts)))
        {
            admin.MapPost("", (IssueServiceClientRequest req, HttpContext http, ServiceClientService svc, IServiceAccounts accounts,
                    ILoggerFactory loggers, CancellationToken ct)
                => ServiceClientAdministrationHandlers.IssueAsync(req, http, svc, accounts, loggers, ct));
            admin.MapPost("/{id:guid}/rotate", (Guid id, HttpContext http, ServiceClientService svc, IServiceAccounts accounts,
                    ILoggerFactory loggers, CancellationToken ct)
                => ServiceClientAdministrationHandlers.RotateAsync(id, http, svc, accounts, loggers, ct));
            admin.MapPatch("/{id:guid}/permissions", (Guid id, UpdateServiceClientPermissionsRequest req, HttpContext http,
                    ServiceClientService svc, IServiceAccounts accounts, ILoggerFactory loggers, CancellationToken ct)
                => ServiceClientAdministrationHandlers.UpdatePermissionsAsync(id, req, http, svc, accounts, loggers, ct));
        }
        return admin;
    }

    /// <summary>
    /// Maps sign-in through external providers (turned on by
    /// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuExternalSignIn"/>):
    /// <c>GET /api/auth/external/{provider}?returnUrl=…</c> sends the browser to the provider behind that handler, and
    /// <c>GET /api/auth/external/complete</c> — where the handler sends it back — opens the application's session for the
    /// account <see cref="IExternalLoginResolver"/> names. Both are anonymous: there is no session yet.
    /// <c>POST /api/auth/sign-out?returnUrl=…</c> ends the session — and the person's session at the provider that
    /// opened it, where that provider's handler can sign out (<see cref="UserSessionService.SignOutAsync"/>).
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
        app.MapPost("/api/auth/sign-out", (string? returnUrl, HttpContext http, UserSessionService sessions)
            => ExternalSignInHandlers.SignOutAsync(returnUrl, http, sessions)).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// The owner the request acts for: the person whose session cookie it carries — read from the cookie, not the
    /// request's merged principal, which may also hold a bearer token's.
    /// </summary>
    private static async Task<Guid?> OwnerAsync(HttpContext http)
        => Guid.TryParse(await SessionCookieRequirement.SubjectAsync(http), out var id) ? id : null;
}
