using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

public sealed record TokenResult(bool Ok, string? Error, string? AccessToken, int ExpiresInSeconds, IReadOnlyList<string> Permissions);

/// <summary>
/// Issues short-lived JWTs for the OAuth2 client_credentials grant, scoped to owner∩client permissions. A person's
/// token is issued by <see cref="UserAccessTokenService"/> or <see cref="UserTokenService"/>.
/// </summary>
/// <remarks>Registered by <see cref="IyuIdentityServiceCollectionExtensions.AddIyuServiceClients"/>.</remarks>
public sealed class ServiceClientTokenService
{
    // Dummy hash for a constant-time verify on the not-found/inactive/expired branch below,
    // so that path takes roughly the same time as the wrong-secret path (no timing oracle
    // that lets a caller distinguish "no such client" from "wrong secret").
    private static readonly string _dummyHash = ServiceClientSecrets.Hash("dummy");

    private readonly IServiceClientStore _store;
    private readonly IdentityTokenOptions _opts;
    private readonly TimeProvider _clock;

    private readonly PermissionHierarchy _hierarchy;
    private readonly ILogger<ServiceClientTokenService> _log;

    public ServiceClientTokenService(IServiceClientStore store, IdentityTokenOptions opts, TimeProvider clock,
        PermissionHierarchy hierarchy, ILogger<ServiceClientTokenService> log)
    {
        _store = store; _opts = opts; _clock = clock; _hierarchy = hierarchy; _log = log;
    }

    public Task<TokenResult> IssueClientCredentialsAsync(string clientId, string secret, CancellationToken ct) =>
        IssueClientCredentialsAsync(clientId, secret, scope: null, ct);

    /// <summary>Issues a client-credentials token, optionally narrowed to a requested scope.</summary>
    /// <remarks>
    /// <paramref name="scope"/> is the RFC 6749 §3.3 <c>scope</c> parameter: space-delimited permission codes. Absent or blank,
    /// the token carries every permission the client effectively holds. Present, it must be a subset
    /// of those — the token then carries exactly the requested ones, and a code outside them fails
    /// the request with <c>invalid_scope</c> rather than being silently dropped.
    /// </remarks>
    public async Task<TokenResult> IssueClientCredentialsAsync(string clientId, string secret, string? scope,
        CancellationToken ct)
    {
        var empty = Array.Empty<string>();
        var client = await _store.FindServiceClientByClientIdAsync(clientId, ct);
        var now = _clock.GetUtcNow();
        if (client is null || !client.IsActive || (client.ExpiresAt is { } exp && exp <= now))
        {
            _ = ServiceClientSecrets.Verify(secret, _dummyHash);   // equalize timing with wrong-secret path
            LogRejection(clientId, client is null ? "no such client"
                                 : !client.IsActive ? "client is revoked"
                                 : "client has expired");
            return new(false, "invalid_client", null, 0, empty);
        }
        if (!ServiceClientSecrets.Verify(secret, client.SecretHash))
        {
            LogRejection(clientId, "secret does not match");
            return new(false, "invalid_client", null, 0, empty);
        }

        var ownerPerms = await _store.GetUserPermissionsAsync(client.OwnerUserId, ct);
        var clientPerms = await _store.GetServiceClientPermissionsAsync(client.Id, ct);
        var effective = PermissionScope.Effective(clientPerms, ownerPerms, _hierarchy);

        // A narrower token may name a code the client holds only through implication — a client with orders.write
        // asking for orders.read gets a token that carries orders.read alone. Without a scope, the token carries the
        // client's own codes and the policies know what they imply.
        var requested = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
        var available = _hierarchy.Expand(effective);
        if (requested.Count > 0 && requested.Any(r => !available.Contains(r, StringComparer.Ordinal)))
        {
            LogRejection(clientId, "requested scope exceeds the client's permissions");
            return new(false, "invalid_scope", null, 0, empty);
        }
        var granted = requested.Count > 0
            ? available.Where(p => requested.Contains(p, StringComparer.Ordinal)).ToList()
            : effective;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, client.ClientId),
            new("owner", client.OwnerUserId.ToString()),   // reserved for future owner-scoped JWT authorization; not yet enforced
        };
        claims.AddRange(granted.Select(p => new Claim(_opts.PermissionClaimType, p)));
        // The fingerprint covers the client's full effective set, not the narrowed grant: it records
        // what the client is, so a narrower token is still ended by a change to the client.
        claims.Add(new Claim(IyuIdentityClaims.ServiceClientStampClaimType,
            ServiceClientStamp.Compute(_opts.SigningKey, client.SecretHash, effective)));

        var jwt = IdentityTokenSigning.Sign(_opts, claims, _opts.Lifetime, now);

        await _store.TouchServiceClientAsync(client.Id, now, ct);
        return new(true, null, jwt, (int)_opts.Lifetime.TotalSeconds, granted);
    }

    /// <summary>
    /// Records, for the operator only, which of the indistinguishable rejection causes applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response stays undifferentiated on purpose and this does not change it.</b> One
    /// <c>invalid_client</c> for every cause, with the dummy-hash verification above equalizing
    /// timing, is what stops the endpoint from confirming which client ids exist. That defence is
    /// aimed at the caller.
    /// </para>
    /// <para>
    /// It was also, accidentally, aimed at the operator: nothing recorded the cause anywhere, so
    /// the person running the server had no more information than the attacker and had to read the
    /// credential rows directly to tell a revoked key from a mistyped secret. The log is where that
    /// asymmetry is restored — the side that already has the database gets the answer, the side on
    /// the wire still gets one word.
    /// </para>
    /// <para>
    /// The client id is logged because it is the public half of the credential and the only handle
    /// an operator can search by. The secret never is, in any form: not the value, not its length,
    /// not a hash of it.
    /// </para>
    /// </remarks>
    private void LogRejection(string clientId, string reason) =>
        _log.LogWarning("client_credentials rejected for {ClientId}: {Reason}", clientId, reason);
}
