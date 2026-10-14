using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Met only by a request that carries a valid session cookie — what <see cref="IyuIdentityServiceCollectionExtensions.CookiePolicyName"/>
/// requires.
/// </summary>
/// <remarks>
/// <para>
/// A policy's authentication schemes say which handlers to ask, not which answer is required — and when an endpoint
/// carries several policies, ASP.NET Core asks the union of their schemes and merges what they answer. A management
/// endpoint that meant "the person's own session" by listing the cookie scheme alone was opened to any bearer token the
/// moment a host added a permission policy that lists the bearer scheme too (one for its administrators, say): the
/// bearer's principal met the permission, and nothing asked for the cookie any more.
/// </para>
/// <para>
/// A requirement is combined the other way — every requirement of every policy must be met — so this one cannot be
/// widened by composition. It asks the cookie handler itself, whose answer is cached for the request.
/// </para>
/// </remarks>
internal sealed class SessionCookieRequirement : AuthorizationHandler<SessionCookieRequirement>, IAuthorizationRequirement
{
    public static SessionCookieRequirement Instance { get; } = new();

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SessionCookieRequirement requirement)
    {
        var http = context.Resource switch
        {
            HttpContext h => h,
            _ => null,
        };
        if (http is not null && (await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)).Succeeded)
            context.Succeed(requirement);
    }

    /// <summary>The person whose session cookie the request carries, or <c>null</c> — read from the cookie, not the merged principal.</summary>
    internal static async Task<string?> SubjectAsync(HttpContext http)
        => await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme) is { Succeeded: true } session
            ? IyuIdentityClaims.FindSubject(session.Principal!)
            : null;
}
