namespace Iyu.MainServer.Identity;

public sealed class IdentityTokenOptions
{
    public string SigningKey { get; set; } = default!;   // consumer-injected (>=32 bytes)
    public string Issuer { get; set; } = "iyu";
    public string Audience { get; set; } = "iyu-api";
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);
    public string PermissionClaimType { get; set; } = "perm";
}
