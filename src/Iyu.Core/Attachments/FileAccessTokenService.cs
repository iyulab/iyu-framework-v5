using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iyu.Core.Attachments;

/// <summary>Signs/validates <see cref="FileAccessToken"/> with HMAC-SHA256 (BCL only — no JWT dependency). Format: base64url(json).base64url(hmac).</summary>
public sealed class FileAccessTokenService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock;

    public FileAccessTokenService(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public string Sign(FileAccessToken token, string signingKey)
    {
        ArgumentNullException.ThrowIfNull(token);
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, Json));
        var sig = Base64UrlEncode(Hmac(payload, signingKey));
        return $"{payload}.{sig}";
    }

    public bool TryValidate(string token, string signingKey, out FileAccessToken? result)
    {
        result = null;
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return false;
        if (token.IndexOf('.', dot + 1) >= 0) return false; // exactly one dot

        var payload = token[..dot];
        byte[] providedSig;
        try { providedSig = Base64UrlDecode(token[(dot + 1)..]); }
        catch (FormatException) { return false; }

        var expectedSig = Hmac(payload, signingKey);
        if (!CryptographicOperations.FixedTimeEquals(providedSig, expectedSig)) return false;

        FileAccessToken? parsed;
        try { parsed = JsonSerializer.Deserialize<FileAccessToken>(Base64UrlDecode(payload), Json); }
        catch (Exception ex) when (ex is JsonException or FormatException) { return false; }
        if (parsed is null) return false;
        if (parsed.ExpiresAt < _clock.GetUtcNow()) return false;

        result = parsed;
        return true;
    }

    private static byte[] Hmac(string payloadBase64, string key)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return h.ComputeHash(Encoding.ASCII.GetBytes(payloadBase64));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t += (t.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(t);
    }
}
