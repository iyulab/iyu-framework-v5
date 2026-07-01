using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Wires the identity runtime into DI: cookie + JWT bearer dual-scheme authentication,
/// token/service-client services, and per-permission authorization policies from a catalog.
/// Consuming apps remain responsible for registering concrete <see cref="IIdentityStore"/> /
/// <see cref="IServiceClientStore"/> implementations.
/// </summary>
public static class IdentityServiceCollectionExtensions
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

        tokenOptions.PermissionClaimType = permissionClaimType;
        services.AddSingleton(tokenOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IdentityTokenService>();
        services.AddScoped<ServiceClientService>();

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.SigningKey));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(opts =>
            {
                opts.Cookie.HttpOnly = true;
                opts.SlidingExpiration = true;
                opts.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                opts.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
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
}
