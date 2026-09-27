namespace Iyu.MainServer.Identity;

/// <summary>
/// A client-credentials token request, however it arrived — the RFC 6749 form body, HTTP Basic
/// client authentication, or the JSON body this endpoint accepted before it took the standard shapes.
/// </summary>
public sealed record TokenRequest(string? ClientId, string? ClientSecret, string? Grant_Type, string? Scope = null);
public sealed record TokenResponse(string access_token, string token_type, int expires_in, string scope);
