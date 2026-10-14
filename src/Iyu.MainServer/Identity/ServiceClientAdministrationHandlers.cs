using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// An administrator acting on a service account's behalf (<see cref="IServiceAccounts"/>): issuing a credential held
/// under the service account, rotating its secret, replacing its permissions. Each one is asked of the owner first — a
/// person's account is refused, since acting in their name would hand the administrator their permissions.
/// </summary>
internal static class ServiceClientAdministrationHandlers
{
    private const string NotServiceAccount = "owner_not_service_account";

    public static async Task<IResult> IssueAsync(IssueServiceClientRequest req, HttpContext http, ServiceClientService svc,
        IServiceAccounts accounts, ILoggerFactory loggers, CancellationToken ct)
    {
        if (await AdministratorAsync(http) is not { } administrator) return Results.Unauthorized();
        if (req.OwnerUserId == Guid.Empty) return Results.BadRequest(new { error = "owner_required" });
        if (!await accounts.IsServiceAccountAsync(req.OwnerUserId, ct)) return Refused(loggers, administrator, req.OwnerUserId, "issue");

        var r = await svc.IssueForAsync(req.OwnerUserId, administrator, req.DisplayName, req.Permissions ?? [], req.ExpiresAt, ct);
        if (!r.Ok)
            return Results.BadRequest(new { error = r.Error, exceeding = r.Exceeding });
        Log(loggers).LogInformation("Service client {ClientId} issued for service account {Owner} by administrator {Administrator}",
            r.Id, req.OwnerUserId, administrator);
        return Results.Created($"/api/admin/service-clients/{r.Id}",
            new { id = r.Id, clientId = r.ClientId, secret = r.PlaintextSecret });   // the secret, once
    }

    public static async Task<IResult> RotateAsync(Guid id, HttpContext http, ServiceClientService svc, IServiceAccounts accounts,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (await AdministratorAsync(http) is not { } administrator) return Results.Unauthorized();
        if (await svc.OwnerOfAsync(id, ct) is not { } owner) return Results.NotFound();
        if (!await accounts.IsServiceAccountAsync(owner, ct)) return Refused(loggers, administrator, owner, "rotate");

        var r = await svc.RotateAsync(id, owner, ct);
        if (!r.Ok) return Results.NotFound();
        Log(loggers).LogInformation("Service client {ClientId} of service account {Owner} rotated by administrator {Administrator}",
            id, owner, administrator);
        return Results.Ok(new { secret = r.PlaintextSecret });
    }

    public static async Task<IResult> UpdatePermissionsAsync(Guid id, UpdateServiceClientPermissionsRequest req, HttpContext http,
        ServiceClientService svc, IServiceAccounts accounts, ILoggerFactory loggers, CancellationToken ct)
    {
        if (await AdministratorAsync(http) is not { } administrator) return Results.Unauthorized();
        if (await svc.OwnerOfAsync(id, ct) is not { } owner) return Results.NotFound();
        if (!await accounts.IsServiceAccountAsync(owner, ct)) return Refused(loggers, administrator, owner, "re-scope");

        var r = await svc.UpdatePermissionsAsync(id, owner, req.Permissions ?? [], ct);
        if (r.Error == "permissions_exceed_owner")
            return Results.BadRequest(new { error = r.Error, exceeding = r.Exceeding });
        if (!r.Ok) return Results.NotFound();
        Log(loggers).LogInformation(
            "Service client {ClientId} of service account {Owner} re-scoped to {Permissions} by administrator {Administrator}",
            id, owner, req.Permissions ?? [], administrator);
        return Results.NoContent();
    }

    /// <summary>
    /// A refusal to act in a person's name — logged, since an administrator reaching for person accounts is the very
    /// incident this surface's audit is for.
    /// </summary>
    private static IResult Refused(ILoggerFactory loggers, Guid administrator, Guid owner, string act)
    {
        Log(loggers).LogWarning("Administrator {Administrator} refused to {Act} a service client for account {Owner}: not a service account",
            administrator, act, owner);
        return Results.Json(new { error = NotServiceAccount }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>The administrator: the person whose session cookie the request carries (the group's policy met them).</summary>
    private static async Task<Guid?> AdministratorAsync(HttpContext http)
        => Guid.TryParse(await SessionCookieRequirement.SubjectAsync(http), out var id) ? id : null;

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger("Iyu.MainServer.Identity.ServiceClientAdministration");
}
