using Iyu.Core.Identity;

namespace Iyu.MainServer.Identity;

/// <summary>
/// The account lookup a local password check needs — the one port
/// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuUserCredentials"/> needs.
/// </summary>
public interface IUserCredentialStore
{
    /// <summary>
    /// The account with this name, active or not — <see cref="UserCredentialService"/> reads
    /// <see cref="IUser.IsActive"/> itself, and tells the application an inactive account apart from an unknown one.
    /// </summary>
    Task<IUser?> FindUserByUsernameAsync(string username, CancellationToken ct);
}
