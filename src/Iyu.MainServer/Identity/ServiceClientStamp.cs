using System.Security.Cryptography;
using System.Text;

namespace Iyu.MainServer.Identity;

/// <summary>
/// A fingerprint of the parts of a service client that its tokens depend on — the secret and the
/// effective permission set. Rotating the secret or replacing the permissions changes it, which is
/// what lets a token issued before the change be told apart from one issued after.
/// </summary>
/// <remarks>
/// <para>
/// It is derived from state the store already keeps rather than stored as a column of its own, so
/// no store has to learn to bump a counter: <c>rotate</c> already replaces the hash and
/// <c>PATCH permissions</c> already replaces the grant.
/// </para>
/// <para>
/// Keyed with the signing key because it travels inside a readable token: without a key the value
/// would be a plain function of the secret hash, and anyone holding a token could test guesses
/// about that hash against it.
/// </para>
/// </remarks>
internal static class ServiceClientStamp
{
    public static string Compute(string signingKey, string secretHash, IEnumerable<string> effectivePermissions)
    {
        var material = secretHash + "\n" + string.Join("\n",
            effectivePermissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(material));
        return Convert.ToBase64String(mac.AsSpan(0, 16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Matches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
}
