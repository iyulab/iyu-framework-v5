using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Refuses to start a host that has no <see cref="IUserClaimsSource"/>.
/// </summary>
/// <remarks>
/// A person's session — cookie or access token — is assembled from that source. Without it the gap
/// would surface at the first sign-in, as a failure to construct <see cref="UserSessionService"/>;
/// service-provider validation reports it earlier, but only where validation is on. This says it at
/// startup everywhere, and says what to register.
/// </remarks>
internal sealed class UserClaimsSourceGuard : IHostedService
{
    private readonly IServiceProviderIsService _registered;

    public UserClaimsSourceGuard(IServiceProviderIsService registered) => _registered = registered;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_registered.IsService(typeof(IUserClaimsSource)))
            throw new InvalidOperationException(
                $"No {nameof(IUserClaimsSource)} is registered. A signed-in person's session — the session cookie "
                + "or an access token — is assembled from the claims that source answers for the account, so the "
                + "application registers its implementation next to AddIyuIdentity: "
                + $"services.AddScoped<{nameof(IUserClaimsSource)}, YourUserClaimsSource>(). "
                + "An application that signs no person in registers one that answers null.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
