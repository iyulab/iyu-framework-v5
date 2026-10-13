using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Refuses to start a host that asked for external session ends to be honoured without an
/// <see cref="IExternalSessionEndStore"/>.
/// </summary>
/// <remarks>
/// Without the store the first session opened through a provider would fail its next request, and a provider's
/// notice would have nowhere to be kept. This says it at startup, and says what to register.
/// </remarks>
internal sealed class ExternalSessionEndStoreGuard(IServiceProviderIsService registered) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!registered.IsService(typeof(IExternalSessionEndStore)))
            throw new InvalidOperationException(
                $"External session ends are on (AddIyuExternalSessionEnds) but no {nameof(IExternalSessionEndStore)} is "
                + "registered. It remembers which provider sessions have ended, and every instance of the application "
                + "must share it: register your own, or the one over the distributed cache — "
                + $"services.AddSingleton<{nameof(IExternalSessionEndStore)}, {nameof(DistributedCacheExternalSessionEndStore)}>() "
                + "next to AddStackExchangeRedisCache or another IDistributedCache.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
