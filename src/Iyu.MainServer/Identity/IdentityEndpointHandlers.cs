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

    public static async Task<IResult> CreateServiceClientAsync(
        CreateServiceClientRequest req, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var r = await svc.CreateAsync(ownerUserId, req.DisplayName, req.Permissions ?? Array.Empty<string>(), req.ExpiresAt, ct);
        if (!r.Ok)
            return Results.BadRequest(new { error = r.Error, exceeding = r.Exceeding });
        return Results.Created($"/api/service-clients/{r.Id}",
            new { id = r.Id, clientId = r.ClientId, secret = r.PlaintextSecret });   // secret 1회 반환
    }

    public static async Task<IResult> RevokeServiceClientAsync(
        Guid id, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var ok = await svc.RevokeAsync(id, ownerUserId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    public static async Task<IResult> RotateServiceClientAsync(
        Guid id, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var r = await svc.RotateAsync(id, ownerUserId, ct);
        return r.Ok ? Results.Ok(new { secret = r.PlaintextSecret }) : Results.NotFound();
    }
}
