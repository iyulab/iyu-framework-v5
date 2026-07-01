using System.Security.Cryptography;

namespace Iyu.MainServer.Identity;

/// <summary>Generates and verifies service-client credentials. Secret is stored hashed (PBKDF2); plaintext returned once.</summary>
public static class ServiceClientSecrets
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    public static (string clientId, string plaintextSecret, string secretHash) Generate()
    {
        var clientId = "svc_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(secret);
        return (clientId, secret, hash);
    }

    public static string Hash(string plaintextSecret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(plaintextSecret, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string plaintextSecret, string secretHash)
    {
        var parts = secretHash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iters)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(plaintextSecret, salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
