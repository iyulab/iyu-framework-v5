using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// The two endpoints of a sign-in through an external provider (<see cref="IdentityEndpointRouteExtensions.MapIyuExternalSignIn"/>).
/// </summary>
internal static class ExternalSignInHandlers
{
    /// <summary>The key a remote handler records its own scheme name under in the properties it signs in with.</summary>
    private const string AuthSchemeKey = ".AuthScheme";

    /// <summary>
    /// <c>GET /api/auth/external/{provider}?returnUrl=…</c> — sends the browser to the provider behind a remote handler
    /// (OpenID Connect, OAuth); it comes back to <see cref="CompleteAsync"/>.
    /// </summary>
    /// <remarks>
    /// 404 for a scheme that is not a registered remote handler — the session cookie, say, is not a provider. 400 for a
    /// <c>returnUrl</c> that leaves the application.
    /// </remarks>
    public static async Task<IResult> ChallengeAsync(string provider, string? returnUrl, HttpContext http,
        IAuthenticationSchemeProvider schemes)
    {
        if (await schemes.GetSchemeAsync(provider) is not { } scheme
            || ExternalSignInSchemeGuard.RemoteOptionsType(scheme.HandlerType) is null)
            return Results.NotFound();
        if (returnUrl is not null && !IsLocalUrl(returnUrl)) return Results.BadRequest();

        var complete = http.Request.PathBase + IyuExternalSignInDefaults.CompletePath
                       + (returnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl));
        return Results.Challenge(new AuthenticationProperties { RedirectUri = complete }, [scheme.Name]);
    }

    /// <summary>
    /// <c>GET /api/auth/external/complete?returnUrl=…</c> — turns what the provider said into the application's session:
    /// <see cref="IExternalLoginResolver"/> names the local account, <see cref="UserSessionService"/> opens its session,
    /// and the external identity is discarded whatever the outcome.
    /// </summary>
    /// <remarks>
    /// 401 when no external identity is held (the provider was never visited, or its answer expired). 403 when it names
    /// no provider identifier, the resolver refuses it, or the claims source refuses the account it resolved to. On
    /// success, a redirect to <c>returnUrl</c> (local only — 400 otherwise), or to the application root.
    /// </remarks>
    public static async Task<IResult> CompleteAsync(string? returnUrl, HttpContext http, IExternalLoginResolver resolver,
        UserSessionService sessions, ILoggerFactory loggers)
    {
        if (returnUrl is not null && !IsLocalUrl(returnUrl)) return Results.BadRequest();

        var external = await http.AuthenticateAsync(IyuExternalSignInDefaults.AuthenticationScheme);
        if (!external.Succeeded) return Results.Unauthorized();

        var log = loggers.CreateLogger(typeof(ExternalSignInHandlers).FullName!);
        try
        {
            var provider = external.Properties?.Items.TryGetValue(AuthSchemeKey, out var scheme) == true ? scheme : null;
            var key = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? external.Principal.FindFirstValue("sub");
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(key))
            {
                log.LogWarning("external sign-in refused: the external identity names no {Missing}",
                    string.IsNullOrEmpty(provider) ? "provider" : "provider identifier");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var subject = await resolver.ResolveSubjectAsync(new ExternalLogin(provider, key, external.Principal), http.RequestAborted);
            if (string.IsNullOrWhiteSpace(subject))
            {
                log.LogInformation("external sign-in refused: no local account for {ProviderKey} at {Provider}", key, provider);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // UserSessionService logs its own refusal (the claims source declined the account).
            if (!await sessions.SignInAsync(http, subject, ct: http.RequestAborted))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return Results.LocalRedirect(returnUrl ?? "/");
        }
        finally
        {
            await http.SignOutAsync(IyuExternalSignInDefaults.AuthenticationScheme);
        }
    }

    /// <summary>
    /// A path on this application: <c>/x</c> or <c>~/x</c>, never <c>//host</c> or <c>/\host</c> (which a browser
    /// follows off-site) — the test <c>IUrlHelper.IsLocalUrl</c> applies.
    /// </summary>
    internal static bool IsLocalUrl(string url)
    {
        if (url.Length == 0) return false;
        if (url[0] == '/')
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\' && !HasControlCharacter(url.AsSpan(1)));
        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
            return url.Length == 2 || (url[2] != '/' && url[2] != '\\' && !HasControlCharacter(url.AsSpan(2)));
        return false;
    }

    private static bool HasControlCharacter(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
            if (char.IsControl(c)) return true;
        return false;
    }
}
