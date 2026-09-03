using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Iyu.MainServer.Identity;

public sealed record TokenResult(bool Ok, string? Error, string? AccessToken, int ExpiresInSeconds, IReadOnlyList<string> Permissions);

/// <summary>Issues short-lived JWTs for the OAuth2 client_credentials grant, scoped to owner∩client permissions.</summary>
public sealed class IdentityTokenService
{
    // Dummy hash for a constant-time verify on the not-found/inactive/expired branch below,
    // so that path takes roughly the same time as the wrong-secret path (no timing oracle
    // that lets a caller distinguish "no such client" from "wrong secret").
    private static readonly string _dummyHash = ServiceClientSecrets.Hash("dummy");

    private readonly IIdentityStore _store;
    private readonly IdentityTokenOptions _opts;
    private readonly TimeProvider _clock;

    public IdentityTokenService(IIdentityStore store, IdentityTokenOptions opts, TimeProvider clock)
    {
        _store = store; _opts = opts; _clock = clock;
    }

    public async Task<TokenResult> IssueClientCredentialsAsync(string clientId, string secret, CancellationToken ct)
    {
        var empty = Array.Empty<string>();
        var client = await _store.FindServiceClientByClientIdAsync(clientId, ct);
        var now = _clock.GetUtcNow();
        if (client is null || !client.IsActive || (client.ExpiresAt is { } exp && exp <= now))
        {
            _ = ServiceClientSecrets.Verify(secret, _dummyHash);   // equalize timing with wrong-secret path
            return new(false, "invalid_client", null, 0, empty);
        }
        if (!ServiceClientSecrets.Verify(secret, client.SecretHash))
            return new(false, "invalid_client", null, 0, empty);

        var ownerPerms = await _store.GetUserPermissionsAsync(client.OwnerUserId, ct);
        var clientPerms = await _store.GetServiceClientPermissionsAsync(client.Id, ct);
        var effective = PermissionScope.Effective(clientPerms, ownerPerms);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, client.ClientId),
            new("owner", client.OwnerUserId.ToString()),   // reserved for future owner-scoped JWT authorization; not yet enforced
        };
        claims.AddRange(effective.Select(p => new Claim(_opts.PermissionClaimType, p)));

        var jwt = SignToken(claims, _opts.Lifetime, now);

        await _store.TouchServiceClientAsync(client.Id, now, ct);
        return new(true, null, jwt, (int)_opts.Lifetime.TotalSeconds, effective);
    }

    /// <summary>
    /// Signs a JWT for a caller-supplied claim set — the counterpart to
    /// <see cref="IssueClientCredentialsAsync"/> for a human principal the consuming app has
    /// already authenticated by its own means (password check, external IdP, etc.). This service
    /// does not verify credentials; the caller is trusted to have done so before calling.
    /// </summary>
    /// <param name="claims">
    /// The claims to embed (e.g. <c>sub</c>, and any <see cref="IdentityTokenOptions.PermissionClaimType"/>
    /// claims the caller wants enforced) — this method neither adds nor validates business claims.
    /// </param>
    /// <param name="lifetimeOverride">
    /// Overrides <see cref="IdentityTokenOptions.Lifetime"/> for this token. Human clients without a
    /// refresh flow (e.g. a mobile app queuing work offline) often need a longer lifetime than the
    /// short-lived default tuned for service clients.
    /// </param>
    /// <remarks>
    /// Synchronous, unlike <see cref="IssueClientCredentialsAsync"/> — there is no store lookup here,
    /// only signing of the claims the caller already assembled.
    /// </remarks>
    public TokenResult IssueUserToken(IEnumerable<Claim> claims, TimeSpan? lifetimeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var lifetime = lifetimeOverride ?? _opts.Lifetime;
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetimeOverride), lifetime, "Lifetime must be positive.");

        var claimsList = claims as IReadOnlyCollection<Claim> ?? claims.ToList();
        var now = _clock.GetUtcNow();

        var jwt = SignToken(claimsList, lifetime, now);

        var permissions = claimsList
            .Where(c => c.Type == _opts.PermissionClaimType)
            .Select(c => c.Value)
            .ToArray();
        return new(true, null, jwt, (int)lifetime.TotalSeconds, permissions);
    }

    private string SignToken(IEnumerable<Claim> claims, TimeSpan lifetime, DateTimeOffset now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_opts.Issuer, _opts.Audience, claims,
            notBefore: now.UtcDateTime, expires: now.Add(lifetime).UtcDateTime, signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
