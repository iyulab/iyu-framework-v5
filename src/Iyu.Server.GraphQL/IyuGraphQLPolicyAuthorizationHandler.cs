using System.Security.Claims;
using HotChocolate.Authorization;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Bridges HotChocolate's <c>[Authorize]</c>/<c>.Authorize(policy)</c> field directives to the
/// same ASP.NET Core <see cref="IAuthorizationService"/> policy catalog
/// <c>Iyu.MainServer.Identity.AddIyuIdentity</c> already registers for OData. HotChocolate ships
/// the directive and the descriptor extension but no default handler that evaluates it against
/// the request's <see cref="ClaimsPrincipal"/> — without one, an <c>.Authorize(policy)</c> call
/// has nothing to enforce it. Wired automatically by
/// <see cref="IyuGraphQLSchemaBuilder.ApplyTo"/> whenever a registered entity pair uses
/// <c>authorizePolicy</c>.
/// </summary>
internal sealed class IyuGraphQLPolicyAuthorizationHandler : HotChocolate.Authorization.IAuthorizationHandler
{
    public ValueTask<AuthorizeResult> AuthorizeAsync(
        IMiddlewareContext context, AuthorizeDirective directive, CancellationToken cancellationToken)
        => EvaluateAsync(context.Services, directive);

    public async ValueTask<AuthorizeResult> AuthorizeAsync(
        AuthorizationContext context, IReadOnlyList<AuthorizeDirective> directives, CancellationToken cancellationToken)
    {
        foreach (var directive in directives)
        {
            var result = await EvaluateAsync(context.Services, directive);
            if (result != AuthorizeResult.Allowed) return result;
        }
        return AuthorizeResult.Allowed;
    }

    private static async ValueTask<AuthorizeResult> EvaluateAsync(IServiceProvider services, AuthorizeDirective directive)
    {
        var user = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());

        // Roles and a named policy can be combined (HotChocolate's Authorize(policy, roles)
        // overload) — both constraints must hold, so a failing role check short-circuits before
        // the (possibly more expensive) policy lookup.
        if (directive.Roles is { Count: > 0 } roles
            && !(user.Identity?.IsAuthenticated == true && roles.Any(user.IsInRole)))
            return AuthorizeResult.NotAllowed;

        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = directive.Policy is { } named
            ? await policyProvider.GetPolicyAsync(named)
            : await policyProvider.GetDefaultPolicyAsync();

        if (policy is null)
            return directive.Policy is not null ? AuthorizeResult.PolicyNotFound : AuthorizeResult.NoDefaultPolicy;

        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
        return result.Succeeded ? AuthorizeResult.Allowed : AuthorizeResult.NotAllowed;
    }
}
