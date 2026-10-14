using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Met when the person whose session cookie the request carries meets <see cref="Policy"/> — a policy evaluated against
/// the cookie's principal alone.
/// </summary>
/// <remarks>
/// <see cref="SessionCookieRequirement"/> asks only that a session cookie be there. An endpoint that also lists a
/// permission policy is then checked against the request's merged principal, which holds a bearer token's identity
/// beside the cookie's: a bearer token carrying the permission, presented with the session cookie of any account, would
/// meet both. Evaluating the policy against the cookie's principal is what makes the session's owner the one who
/// holds the permission.
/// </remarks>
internal sealed class SessionCookiePolicyRequirement(string policy)
    : AuthorizationHandler<SessionCookiePolicyRequirement>, IAuthorizationRequirement
{
    /// <summary>The policy the person behind the session cookie must meet.</summary>
    public string Policy { get; } = policy;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        SessionCookiePolicyRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return;
        var session = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!session.Succeeded) return;

        var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
        if ((await authorization.AuthorizeAsync(session.Principal!, http, requirement.Policy)).Succeeded)
            context.Succeed(requirement);
    }
}
