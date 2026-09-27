using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Decides, for a validated bearer token, whether the service client it was issued to still stands
/// behind it: the client exists, is active, has not expired, and still has the secret and effective
/// permissions the token was issued under.
/// </summary>
/// <remarks>
/// <para>
/// Signature, issuer, audience and lifetime say only that the framework issued the token and that
/// it has not run out. Revocation, rotation and permission changes happen after issuance, so the
/// token cannot carry the answer — the store has it. Without this check those three operations
/// stopped the next issuance and nothing else.
/// </para>
/// <para>
/// A successful check is reused for <see cref="IdentityTokenOptions.ServiceClientValidationWindow"/>,
/// measured on the framework's <see cref="TimeProvider"/> (the same clock token expiry is issued
/// from). Only successes are reused: a rejected token is looked up again next time, so a client
/// that is fixed takes effect at once. The cache key includes the stamp, so a rotated client's new
/// tokens never ride on an old entry.
/// </para>
/// </remarks>
internal sealed class ServiceClientTokenValidator
{
    private readonly IIdentityStore _store;
    private readonly IdentityTokenOptions _opts;
    private readonly TimeProvider _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ServiceClientTokenValidator> _log;

    public ServiceClientTokenValidator(IIdentityStore store, IdentityTokenOptions opts, TimeProvider clock,
        IMemoryCache cache, ILogger<ServiceClientTokenValidator> log)
    {
        _store = store; _opts = opts; _clock = clock; _cache = cache; _log = log;
    }

    /// <returns><c>null</c> when the token is acceptable, otherwise the reason for the operator log.</returns>
    public async Task<string?> RejectionAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var stamp = principal.FindFirstValue(IyuIdentityClaims.ServiceClientStampClaimType);
        if (stamp is null) return null;   // not a service-client token

        // The bearer handler maps "sub" to NameIdentifier on the way in unless the host turned the
        // mapping off; accept either spelling rather than depend on that setting.
        var clientId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                       ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(clientId)) return "token has a client stamp but no subject";

        var now = _clock.GetUtcNow();
        var key = (typeof(ServiceClientTokenValidator), clientId, stamp);
        if (_cache.TryGetValue(key, out DateTimeOffset checkedUntil) && now < checkedUntil) return null;

        var client = await _store.FindServiceClientByClientIdAsync(clientId, ct);
        string? reason = null;
        if (client is null) reason = "no such client";
        else if (!client.IsActive) reason = "client is revoked";
        else if (client.ExpiresAt is { } exp && exp <= now) reason = "client has expired";
        else
        {
            var ownerPerms = await _store.GetUserPermissionsAsync(client.OwnerUserId, ct);
            var clientPerms = await _store.GetServiceClientPermissionsAsync(client.Id, ct);
            var current = ServiceClientStamp.Compute(_opts.SigningKey, client.SecretHash,
                PermissionScope.Effective(clientPerms, ownerPerms));
            if (!ServiceClientStamp.Matches(current, stamp))
                reason = "secret or permissions changed since the token was issued";
        }

        if (reason is not null)
        {
            _log.LogWarning("service-client token rejected for {ClientId}: {Reason}", clientId, reason);
            return reason;
        }

        var window = _opts.ServiceClientValidationWindow;
        if (window > TimeSpan.Zero)
            _cache.Set(key, now + window, window);   // the cache's own expiry only reclaims memory
        return null;
    }
}
