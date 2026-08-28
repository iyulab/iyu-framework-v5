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

    /// <remarks>The <c>secret</c> in this response is shown once by design and cannot be recovered — rotate
    /// the client if it is lost. The <c>id</c> can be recovered: <c>GET /api/service-clients</c> lists what
    /// the caller owns, which is where rotate and revoke get their handle when the issuing response is gone.</remarks>
    public static async Task<IResult> CreateServiceClientAsync(
        CreateServiceClientRequest req, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var r = await svc.CreateAsync(ownerUserId, req.DisplayName, req.Permissions ?? Array.Empty<string>(), req.ExpiresAt, ct);
        if (!r.Ok)
            return Results.BadRequest(new { error = r.Error, exceeding = r.Exceeding });
        return Results.Created($"/api/service-clients/{r.Id}",
            new { id = r.Id, clientId = r.ClientId, secret = r.PlaintextSecret });   // secret 1회 반환
    }

    /// <summary>Lists the caller's own service clients, revoked ones included and marked inactive.</summary>
    /// <remarks>
    /// Returns <see cref="ServiceClientSummary"/> rather than the stored client: that type has no
    /// secret material on it at all, so "the hash must not be serialised here" holds by construction
    /// instead of by everyone downstream remembering.
    /// </remarks>
    public static async Task<IResult> ListServiceClientsAsync(
        Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
        => Results.Ok(await svc.ListAsync(ownerUserId, ct));

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

    public static async Task<IResult> UpdateServiceClientPermissionsAsync(
        Guid id, UpdateServiceClientPermissionsRequest req, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var r = await svc.UpdatePermissionsAsync(id, ownerUserId, req.Permissions ?? Array.Empty<string>(), ct);
        if (r.Ok) return Results.NoContent();
        if (r.Error == "permissions_exceed_owner")
            return Results.BadRequest(new { error = r.Error, exceeding = r.Exceeding });
        return Results.NotFound();
    }
}
