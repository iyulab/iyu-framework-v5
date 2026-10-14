using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Iyu.MainServer.Identity;

/// <summary>
/// The endpoint a provider posts its logout token to (<see cref="IyuBackChannelLogoutExtensions.MapIyuBackChannelLogout"/>).
/// </summary>
internal static class BackChannelLogoutHandler
{
    /// <summary>The form field the logout token arrives in.</summary>
    private const string LogoutTokenField = "logout_token";

    public static async Task<IResult> ReceiveAsync(string provider, HttpContext http, IAuthenticationSchemeProvider schemes,
        IOptionsMonitor<OpenIdConnectOptions> handlers, BackChannelLogoutTokenValidator validator, SessionEndService sessions)
    {
        // A cached answer must never stand in for a later notice.
        http.Response.Headers.CacheControl = "no-store";

        if (await schemes.GetSchemeAsync(provider) is not { } scheme
            || !typeof(OpenIdConnectHandler).IsAssignableFrom(scheme.HandlerType))
            return Results.NotFound();

        if (!http.Request.HasFormContentType)
            return Refuse("The request is not a form post.");
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!form.TryGetValue(LogoutTokenField, out var tokens) || tokens.Count != 1 || string.IsNullOrEmpty(tokens[0]))
            return Refuse("The request carries no logout_token.");

        var outcome = await validator.ValidateAsync(handlers.Get(scheme.Name), tokens[0]!, http.RequestAborted);
        if (outcome.SessionId is null) return Refuse(outcome.Refusal!);

        // Whether or not anyone here holds a session opened from it: a session that is already over is a success.
        await sessions.EndProviderSessionAsync(scheme.Name, outcome.SessionId, http.RequestAborted);
        return Results.Ok();
    }

    private static IResult Refuse(string why)
        => Results.Json(new { error = "invalid_request", error_description = why }, statusCode: StatusCodes.Status400BadRequest);
}
