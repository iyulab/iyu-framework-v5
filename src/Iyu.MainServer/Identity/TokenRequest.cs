namespace Iyu.MainServer.Identity;

public sealed record TokenRequest(string? ClientId, string? ClientSecret, string? Grant_Type);
public sealed record TokenResponse(string access_token, string token_type, int expires_in, string scope);
