using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Wires the identity runtime into DI: cookie + JWT bearer dual-scheme authentication,
/// token/service-client services, the services that open a person's session (cookie or access token), and per-permission
/// authorization policies from a catalog. Consuming apps remain responsible for registering concrete
/// <see cref="IIdentityStore"/> / <see cref="IServiceClientStore"/> implementations and their
/// <see cref="IUserClaimsSource"/>.
/// </summary>
public static class IyuIdentityServiceCollectionExtensions
{
    /// <summary>Named authorization policy for the owner-scoped service-client management endpoints:
    /// cookie-only (human operators), never satisfied by the JWT bearer scheme service clients use.</summary>
    public const string CookiePolicyName = "iyu:identity:cookie";

    public static IServiceCollection AddIyuIdentity(
        this IServiceCollection services,
        IdentityTokenOptions tokenOptions,
        IEnumerable<string> permissionCatalog,
        string permissionClaimType = "perm")
    {
        ArgumentNullException.ThrowIfNull(tokenOptions);
        if (string.IsNullOrEmpty(tokenOptions.SigningKey) ||
            System.Text.Encoding.UTF8.GetByteCount(tokenOptions.SigningKey) < 32)
            throw new ArgumentException(
                "IdentityTokenOptions.SigningKey must be at least 32 bytes (256 bits) for HS256.",
                nameof(tokenOptions));
        if (tokenOptions.SessionRevalidationInterval < TimeSpan.Zero)
            throw new ArgumentException(
                "IdentityTokenOptions.SessionRevalidationInterval cannot be negative. TimeSpan.Zero re-reads a "
                + "session's claims on every request.",
                nameof(tokenOptions));

        tokenOptions.PermissionClaimType = permissionClaimType;
        services.AddSingleton(tokenOptions);
        // TryAdd, not Add — see the same call in Iyu.FileServer's gateway registration: a host that
        // registered its own TimeProvider first would otherwise have it silently replaced here,
        // which is what token expiry and secret-rotation timestamps are read from.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IdentityTokenService>();
        services.AddScoped<ServiceClientService>();
        services.AddMemoryCache();
        services.AddScoped<ServiceClientTokenValidator>();

        // A person's session is assembled from the app's claims source, not by hand — see UserSessionService.
        // The source is the app's to register; a host without one is refused at startup.
        services.TryAddScoped<UserSessionService>();
        services.TryAddScoped<UserAccessTokenService>();
        services.AddHostedService<UserClaimsSourceGuard>();

        // Cookie sessions are only as durable as the Data Protection key ring that seals them — refuse to start
        // where that ring would be lost with the container. See CookieKeyRingValidator.
        services.AddSingleton<IValidateOptions<KeyManagementOptions>, CookieKeyRingValidator>();
        services.AddOptions<KeyManagementOptions>().ValidateOnStart();

        // The session cookie is also the default sign-in scheme, which is where an external sign-in handler puts
        // the identity it receives unless told otherwise — refuse to start that way. See ExternalSignInSchemeGuard.
        services.AddHostedService<ExternalSignInSchemeGuard>();

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.SigningKey));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(opts =>
            {
                opts.Cookie.HttpOnly = true;
                opts.SlidingExpiration = true;
                opts.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                opts.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                // A session is re-read from the claims source while it lives — see UserSessionService. Whether one
                // is due is decided from the cookie alone; the scoped service is resolved only when it is.
                opts.Events.OnValidatePrincipal = ctx => UserSessionService.IsRevalidationDue(ctx, tokenOptions)
                    ? ctx.HttpContext.RequestServices.GetRequiredService<UserSessionService>().RevalidateAsync(ctx)
                    : Task.CompletedTask;
            })
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = tokenOptions.Issuer,
                    ValidateAudience = true, ValidAudience = tokenOptions.Audience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                    ValidateLifetime = true,
                };
                // Revoke, rotate and PATCH permissions act on the store; the token was signed before
                // any of them. This is where the two meet — see ServiceClientTokenValidator.
                if (tokenOptions.ValidateServiceClientTokens)
                    opts.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = async ctx =>
                        {
                            var validator = ctx.HttpContext.RequestServices.GetRequiredService<ServiceClientTokenValidator>();
                            var reason = await validator.RejectionAsync(ctx.Principal!, ctx.HttpContext.RequestAborted);
                            if (reason is not null)
                                ctx.Fail("The service client behind this token has been revoked, rotated or re-scoped.");
                        },
                    };
            });

        services.AddAuthorization(opts =>
        {
            foreach (var perm in permissionCatalog.Distinct())
                opts.AddPolicy(perm, p => p
                    .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme)
                    .RequireClaim(permissionClaimType, perm));
            opts.AddPolicy(CookiePolicyName, p => p
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());
            opts.FallbackPolicy = new AuthorizationPolicyBuilder(
                    CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().Build();
        });
        return services;
    }

    /// <summary>
    /// Turns on refresh tokens for people: registers <see cref="UserTokenService"/>, which the app's own
    /// sign-in endpoint calls, and makes <c>POST /api/auth/token</c> accept <c>grant_type=refresh_token</c>.
    /// </summary>
    /// <remarks>
    /// The app registers <see cref="IRefreshTokenStore"/> itself, with whatever lifetime its storage needs
    /// (<see cref="IUserClaimsSource"/> is already required by <see cref="AddIyuIdentity"/>). A missing store
    /// is a startup error under service-provider validation (on by default in Development), and otherwise an
    /// <see cref="InvalidOperationException"/> naming it the first time the service is resolved. Apps that do
    /// not call this need no store: the grant answers <c>unsupported_grant_type</c>.
    /// </remarks>
    public static IServiceCollection AddIyuRefreshTokens(this IServiceCollection services)
    {
        services.TryAddScoped<UserTokenService>();
        return services;
    }
}
