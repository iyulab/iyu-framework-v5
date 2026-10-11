using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Signs the application's own access tokens — a service client's (<see cref="ServiceClientTokenService"/>) and a
/// person's (<see cref="UserAccessTokenService"/>) — with the key and issuer the bearer scheme validates against.
/// </summary>
/// <remarks>
/// Needs no store: what a token says is decided by whoever calls this. Not public for the same reason — a token signed
/// over claims an application put together would carry whatever permissions it wrote, for as long as it lives.
/// </remarks>
internal static class IdentityTokenSigning
{
    public static string Sign(IdentityTokenOptions opts, IEnumerable<Claim> claims, TimeSpan lifetime, DateTimeOffset now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(opts.Issuer, opts.Audience, claims,
            notBefore: now.UtcDateTime, expires: now.Add(lifetime).UtcDateTime, signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
