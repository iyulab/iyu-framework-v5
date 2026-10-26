using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.VaultAi;

/// <summary>
/// Puts every route the module owns — the <c>/api/vault-ai</c> API, the viewer at <c>BasePath</c>, and the report
/// folders served at <c>{BasePath}-files</c> — behind one authorization policy. The module is a single middleware
/// rather than endpoints, so the host cannot attach <c>RequireAuthorization</c> to it; this is where the policy is
/// applied, the way the authorization middleware applies it to an endpoint.
/// </summary>
internal static class VaultAiAuthorization
{
    internal const string ApiPrefix = "/api/vault-ai/";

    /// <summary>True for every path the module answers. Deliberately broader than the module's own routing
    /// (<c>BasePath</c> as a bare prefix covers the viewer and <c>{BasePath}-files</c> alike), so a path the module
    /// would serve can never fall outside the guard.</summary>
    internal static bool Guards(PathString path, string basePath)
    {
        var value = path.Value ?? "";
        return value.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a browser says this request was started by another site — a link, form or script on a page
    /// that is not this one. A current browser sends <c>Sec-Fetch-Site</c> on every request: <c>same-origin</c> for the
    /// viewer's own calls, <c>none</c> for a typed URL or bookmark. Without it (an older browser, or a client that is not a
    /// browser) the request's <c>Origin</c> or <c>Referer</c> is read the same way when one is present; a client that
    /// sends none of them — a monitor, a script — is not refused.</summary>
    internal static bool IsInitiatedByAnotherSite(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
            return !site.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                && !site.Equals("none", StringComparison.OrdinalIgnoreCase);

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0)
            return !SameOrigin(origin, request);   // the literal "null" (an opaque origin) is not a URL: another site

        var referer = request.Headers.Referer.ToString();
        return referer.Length > 0 && !SameOrigin(referer, request);
    }

    private static bool SameOrigin(string source, HttpRequest request)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        var port = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return uri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == port;
    }

    /// <summary>Fails at startup, not on the first request, when <see cref="VaultAiSettings.AuthorizationPolicy"/>
    /// names a policy that is not registered.</summary>
    internal static void EnsureConfigured(IServiceProvider services, VaultAiSettings settings)
    {
        if (settings.AllowAnonymous || settings.AuthorizationPolicy is not { Length: > 0 } name) return;

        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        if (provider.GetPolicyAsync(name).GetAwaiter().GetResult() is null)
            throw new InvalidOperationException(
                $"VaultAi:AuthorizationPolicy names the policy '{name}', but no such authorization policy is registered. " +
                "Register it with services.AddAuthorization(o => o.AddPolicy(...)), or leave the setting empty to require " +
                "any signed-in user.");
    }

    /// <summary>Runs the policy for this request. Returns false after writing the challenge or forbid response.</summary>
    internal static async Task<bool> AuthorizeAsync(HttpContext context, VaultAiSettings settings)
    {
        if (settings.AllowAnonymous) return true;

        var provider = context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        var evaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();

        var policy = await AuthorizationPolicy.CombineAsync(provider,
            [new AuthorizeAttribute { Policy = string.IsNullOrEmpty(settings.AuthorizationPolicy) ? null : settings.AuthorizationPolicy }])
            ?? throw new InvalidOperationException("The VaultAi authorization policy resolved to nothing.");

        var authentication = await evaluator.AuthenticateAsync(policy, context);
        var result = await evaluator.AuthorizeAsync(policy, authentication, context, resource: null);

        if (result.Succeeded) return true;

        if (result.Challenged)
        {
            if (policy.AuthenticationSchemes.Count > 0)
                foreach (var scheme in policy.AuthenticationSchemes) await context.ChallengeAsync(scheme);
            else
                await context.ChallengeAsync();
        }
        else
        {
            if (policy.AuthenticationSchemes.Count > 0)
                foreach (var scheme in policy.AuthenticationSchemes) await context.ForbidAsync(scheme);
            else
                await context.ForbidAsync();
        }
        return false;
    }
}
