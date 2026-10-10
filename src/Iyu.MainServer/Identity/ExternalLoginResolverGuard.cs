using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Refuses to start a host that turned external sign-in on without an <see cref="IExternalLoginResolver"/>.
/// </summary>
/// <remarks>
/// Without it every external sign-in would fail at the completion endpoint, after the person has already been to the
/// provider and back. This says it at startup, and says what to register.
/// </remarks>
internal sealed class ExternalLoginResolverGuard(IServiceProviderIsService registered) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!registered.IsService(typeof(IExternalLoginResolver)))
            throw new InvalidOperationException(
                $"External sign-in is on (AddIyuExternalSignIn) but no {nameof(IExternalLoginResolver)} is registered. "
                + "It decides which local account an external sign-in is — linking or provisioning is the "
                + "application's policy: "
                + $"services.AddScoped<{nameof(IExternalLoginResolver)}, YourExternalLoginResolver>().");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
