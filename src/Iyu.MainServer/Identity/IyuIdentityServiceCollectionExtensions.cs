using System.Text;
using Iyu.Core.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Wires the identity runtime into DI: cookie + JWT bearer dual-scheme authentication,
/// token/service-client services, the local password check, the services that open a person's session (cookie or access
/// token), and per-permission
/// authorization policies from a catalog. Consuming apps remain responsible for registering concrete
/// <see cref="IIdentityStore"/> / <see cref="IServiceClientStore"/> implementations and their
/// <see cref="IUserClaimsSource"/>.
/// </summary>
public static class IyuIdentityServiceCollectionExtensions
{
    /// <summary>Named authorization policy for the owner-scoped service-client management endpoints:
    /// cookie-only (human operators), never satisfied by the JWT bearer scheme service clients use.</summary>
    public const string CookiePolicyName = "iyu:identity:cookie";

    /// <param name="services">The service collection.</param>
    /// <param name="tokenOptions">Signing key, issuer, audience and lifetimes.</param>
    /// <param name="permissionCatalog">Every permission code the application uses. Each becomes an authorization policy
    /// of the same name.</param>
    /// <param name="permissionClaimType">The claim type permission codes travel in.</param>
    /// <param name="permissionImplications">Codes that also count as holding others — <c>new("orders.write",
    /// "orders.read")</c> lets an <c>orders.write</c> holder satisfy the <c>orders.read</c> policy without being given
    /// <c>orders.read</c> too. Transitive; every code named must be in the catalog. See <see cref="PermissionHierarchy"/>.</param>
    public static IServiceCollection AddIyuIdentity(
        this IServiceCollection services,
        IdentityTokenOptions tokenOptions,
        IEnumerable<string> permissionCatalog,
        string permissionClaimType = "perm",
        IEnumerable<PermissionImplication>? permissionImplications = null)
    {
        ArgumentNullException.ThrowIfNull(tokenOptions);
        ArgumentNullException.ThrowIfNull(permissionCatalog);
        var catalog = permissionCatalog.Distinct(StringComparer.Ordinal).ToList();
        var hierarchy = new PermissionHierarchy(catalog, permissionImplications ?? []);
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
        services.AddSingleton(hierarchy);
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

        // A local password check. TryAdd for the hasher: an app whose stored hashes are in another format registers its
        // own, before or after this call.
        services.TryAddSingleton<IPasswordHasher<IUser>, PasswordHasher<IUser>>();
        services.TryAddScoped<UserCredentialService>();

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
            // A code's policy is met by the code itself or by any code that implies it.
            foreach (var perm in catalog)
                opts.AddPolicy(perm, p => p
                    .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme)
                    .RequireClaim(permissionClaimType, hierarchy.SatisfiedBy(perm)));
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

    /// <summary>
    /// Turns on sign-in through external providers: a cookie scheme of its own holds what a provider said until the
    /// application has decided which local account that is, and <see cref="IdentityEndpointRouteExtensions.MapIyuExternalSignIn"/>
    /// maps the endpoints that start a sign-in and complete it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application still registers each provider's handler — <c>AddOpenIdConnect("oidc", …)</c> and the like — and
    /// its <see cref="IExternalLoginResolver"/> (a host without one is refused at startup). A handler needs no
    /// <c>SignInScheme</c>: <see cref="IyuExternalSignInDefaults.AuthenticationScheme"/> becomes the default sign-in
    /// scheme, which is where remote handlers put what they receive — never the session cookie.
    /// </para>
    /// <para>
    /// The same default means a <c>HttpContext.SignInAsync(principal)</c> that names no scheme lands there too, not in the
    /// session. A person's session is opened by <see cref="UserSessionService.SignInAsync"/>, which names its scheme.
    /// Signing out keeps the session cookie as its default, so a <c>SignOutAsync()</c> that names no scheme still ends
    /// the session.
    /// </para>
    /// <para>
    /// The external cookie lives five minutes — long enough to come back from the provider — and is removed by the
    /// completion endpoint whatever its outcome.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddIyuExternalSignIn(this IServiceCollection services)
    {
        services.AddAuthentication(o =>
            {
                o.DefaultSignInScheme = IyuExternalSignInDefaults.AuthenticationScheme;
                // Signing out falls back to the sign-in default when it has none of its own — without this, an
                // application's SignOutAsync() that names no scheme would end the external cookie and leave the session.
                o.DefaultSignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(IyuExternalSignInDefaults.AuthenticationScheme, o =>
            {
                o.Cookie.Name = ".Iyu.External";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                o.SlidingExpiration = false;
                // Nothing challenges this scheme; if something did, it answers with a status rather than a login page.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        services.AddHostedService<ExternalLoginResolverGuard>();
        return services;
    }
}
