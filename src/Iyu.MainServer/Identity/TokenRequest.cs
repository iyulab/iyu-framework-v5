namespace Iyu.MainServer.Identity;

/// <summary>
/// A token request — client credentials or a refresh token — however it arrived: the RFC 6749 form
/// body, HTTP Basic client authentication, or the JSON body this endpoint accepted before it took the
/// standard shapes.
/// </summary>
public sealed record TokenRequest(string? ClientId, string? ClientSecret, string? Grant_Type, string? Scope = null,
    string? Refresh_Token = null);
public sealed record TokenResponse(string access_token, string token_type, int expires_in, string scope);

/// <summary>The answer to a refresh-token grant (RFC 6749 §5.1): the next access token and the refresh token that replaces the one presented.</summary>
public sealed record RefreshTokenResponse(string access_token, string token_type, int expires_in, string refresh_token, string scope);
