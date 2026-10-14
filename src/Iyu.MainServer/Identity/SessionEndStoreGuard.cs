using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Refuses to start a host that asked for session ends to be honoured without an <see cref="ISessionEndStore"/>.
/// </summary>
/// <remarks>
/// Without the store the first session presented would fail its request, and a sign-out or a provider's notice would
/// have nowhere to be kept. This says it at startup, and says what to register.
/// </remarks>
internal sealed class SessionEndStoreGuard(IServiceProviderIsService registered) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!registered.IsService(typeof(ISessionEndStore)))
            throw new InvalidOperationException(
                $"Session ends are on (AddIyuSessionEnds) but no {nameof(ISessionEndStore)} is registered. It "
                + "remembers which sessions have ended — signed out, revoked, or ended at an external provider — and "
                + "every instance of the application must share it: register your own, or the one over the "
                + $"distributed cache — services.AddSingleton<{nameof(ISessionEndStore)}, "
                + $"{nameof(DistributedCacheSessionEndStore)}>() next to AddStackExchangeRedisCache or another "
                + "IDistributedCache.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
