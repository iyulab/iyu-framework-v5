using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Met only by a request that carries a valid session cookie whose person meets every other authorization requirement
/// the endpoint carries — what <see cref="IyuIdentityServiceCollectionExtensions.CookiePolicyName"/> requires.
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
/// <para>
/// Asking for the cookie is not enough on its own. These endpoints act for the cookie's person, but the host's policy
/// on the same endpoint is checked against the merged principal: a bearer token holding the permission, presented
/// beside the session cookie of an account that does not hold it, met the policy while the endpoint acted in the other
/// account's name. So every other requirement the endpoint carries is checked again against the cookie's principal
/// alone — whoever the endpoint acts for is the one who meets its policies.
/// </para>
/// </remarks>
internal sealed class SessionCookieRequirement : AuthorizationHandler<SessionCookieRequirement>, IAuthorizationRequirement
{
    public static SessionCookieRequirement Instance { get; } = new();

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SessionCookieRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return;
        var session = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (session.Succeeded && await EndpointRequirementsMetByAsync(http, session.Principal!))
            context.Succeed(requirement);
    }

    /// <summary>
    /// Whether <paramref name="person"/> alone meets every authorization requirement of the endpoint being reached,
    /// other than this one — gathered the way the authorization middleware gathers them: named and inline policies, and
    /// requirements declared directly on the endpoint.
    /// </summary>
    private static async Task<bool> EndpointRequirementsMetByAsync(HttpContext http, ClaimsPrincipal person)
    {
        if (http.GetEndpoint() is not { } endpoint) return true;

        var metadata = endpoint.Metadata;
        var combined = await AuthorizationPolicy.CombineAsync(
            http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>(),
            metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata.GetOrderedMetadata<AuthorizationPolicy>());
        var requirements = (combined?.Requirements ?? [])
            .Concat(metadata.GetOrderedMetadata<IAuthorizationRequirementData>().SelectMany(d => d.GetRequirements()))
            .Where(r => r is not SessionCookieRequirement)
            .ToList();
        if (requirements.Count == 0) return true;

        var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(person, http, requirements)).Succeeded;
    }

    /// <summary>The person whose session cookie the request carries, or <c>null</c> — read from the cookie, not the merged principal.</summary>
    internal static async Task<string?> SubjectAsync(HttpContext http)
        => await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme) is { Succeeded: true } session
            ? IyuIdentityClaims.FindSubject(session.Principal!)
            : null;
}
