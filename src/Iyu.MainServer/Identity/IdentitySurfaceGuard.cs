using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Refuses to start a host that registered a surface's store without turning the surface on.
/// </summary>
/// <remarks>
/// Each optional identity surface asks for its own store, and the store alone does nothing: an
/// <see cref="IServiceClientStore"/> without <see cref="IyuIdentityServiceCollectionExtensions.AddIyuServiceClients"/> leaves
/// <c>client_credentials</c> answering <c>unsupported_grant_type</c> and no management endpoints — every machine
/// integration fails the way a revoked credential does, with nothing saying why. This says it at startup, and says
/// what to call.
/// </remarks>
internal sealed class IdentitySurfaceGuard : IHostedService
{
    private readonly IServiceProviderIsService _registered;

    public IdentitySurfaceGuard(IServiceProviderIsService registered) => _registered = registered;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Refuse(typeof(IServiceClientStore), typeof(ServiceClientService),
            nameof(IyuIdentityServiceCollectionExtensions.AddIyuServiceClients));
        Refuse(typeof(IUserCredentialStore), typeof(UserCredentialService),
            nameof(IyuIdentityServiceCollectionExtensions.AddIyuUserCredentials));
        Refuse(typeof(ISessionEndStore), typeof(SessionEndService),
            nameof(IyuIdentityServiceCollectionExtensions.AddIyuSessionEnds));
        return Task.CompletedTask;
    }

    private void Refuse(Type store, Type surface, string call)
    {
        if (_registered.IsService(store) && !_registered.IsService(surface))
            throw new InvalidOperationException(
                $"An {store.Name} is registered, but the surface that uses it is not turned on. Call "
                + $"services.{call}() next to AddIyuIdentity — or remove the store registration if the surface is not "
                + "wanted.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
