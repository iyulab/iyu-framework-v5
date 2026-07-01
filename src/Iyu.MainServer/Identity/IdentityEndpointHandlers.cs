using Microsoft.AspNetCore.Http;

namespace Iyu.MainServer.Identity;

/// <summary>Testable endpoint handlers for the identity runtime (registered by AddIyuIdentity / MapIyuIdentity).</summary>
public static class IdentityEndpointHandlers
{
    public static async Task<IResult> TokenAsync(TokenRequest req, IdentityTokenService tokens, CancellationToken ct)
    {
        if (req.Grant_Type != "client_credentials")
            return Results.BadRequest(new { error = "unsupported_grant_type" });
        if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.ClientSecret))
            return Results.BadRequest(new { error = "invalid_request" });

        var r = await tokens.IssueClientCredentialsAsync(req.ClientId!, req.ClientSecret!, ct);
        if (!r.Ok) return Results.Unauthorized();
        return Results.Ok(new TokenResponse(r.AccessToken!, "Bearer", r.ExpiresInSeconds, string.Join(' ', r.Permissions)));
    }
}
