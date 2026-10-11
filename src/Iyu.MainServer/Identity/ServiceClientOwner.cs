namespace Iyu.MainServer.Identity;

/// <summary>
/// What a service client's owner may do — read from <see cref="IUserClaimsSource"/>, the source the owner's own session
/// is assembled from, so a client never holds more than the person who issued it holds right now.
/// </summary>
internal static class ServiceClientOwner
{
    /// <returns>The owner's permission codes, or <c>null</c> when the source refuses the owner (no account, deactivated).</returns>
    public static async Task<IReadOnlyList<string>?> PermissionsAsync(IUserClaimsSource source, IdentityTokenOptions opts,
        Guid owner, CancellationToken ct)
    {
        var claims = await source.GetClaimsAsync(owner.ToString(), ct);
        return claims?.Where(c => c.Type == opts.PermissionClaimType).Select(c => c.Value)
            .Distinct(StringComparer.Ordinal).ToList();
    }
}
