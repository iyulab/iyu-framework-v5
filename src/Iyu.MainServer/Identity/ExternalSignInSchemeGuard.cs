using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Refuses to start a host in which an external sign-in handler (OpenID Connect, OAuth and the like) would sign the
/// identity it receives straight into the application's session cookie.
/// </summary>
/// <remarks>
/// <para>
/// A remote handler persists what the provider told it into its <c>SignInScheme</c>, and when none is named that is
/// the default scheme — which <see cref="IyuIdentityServiceCollectionExtensions.AddIyuIdentity"/> makes the session
/// cookie every authorization policy here reads. The provider's identity would then <i>be</i> the session: anyone the
/// provider authenticates passes "is signed in" with or without a local account, and a claim the provider sends under
/// the permission claim type passes the permission policies. Nothing fails; the application simply never got to
/// decide who the person is or what they may do.
/// </para>
/// <para>
/// So the composition is refused rather than documented around. The handler is given a scheme of its own, and the
/// application opens its session after resolving the account (README «External sign-in»). A deployment that means
/// the provider's identity to be its session — no local accounts, authorization that does not rest on the
/// permission claims — says so with <see cref="IdentityTokenOptions.AllowExternalIdentityAsSession"/>.
/// </para>
/// <para>
/// Checked when the host starts, not when options are built: a remote handler's options resolve their default
/// scheme from the authentication options, so reading them from inside that validation would re-enter it.
/// </para>
/// </remarks>
internal sealed class ExternalSignInSchemeGuard(
    IAuthenticationSchemeProvider schemes, IServiceProvider services, IdentityTokenOptions tokenOptions) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (tokenOptions.AllowExternalIdentityAsSession) return;

        foreach (var scheme in await schemes.GetAllSchemesAsync())
        {
            if (RemoteOptionsType(scheme.HandlerType) is not { } optionsType) continue;

            var monitor = services.GetRequiredService(typeof(IOptionsMonitor<>).MakeGenericType(optionsType));
            var options = (RemoteAuthenticationOptions)monitor.GetType()
                .GetMethod(nameof(IOptionsMonitor<object>.Get))!
                .Invoke(monitor, [scheme.Name])!;

            if (options.SignInScheme == CookieAuthenticationDefaults.AuthenticationScheme)
                throw new InvalidOperationException(
                    $"The authentication scheme '{scheme.Name}' signs the identity it receives into "
                    + $"'{CookieAuthenticationDefaults.AuthenticationScheme}', the application's session (AddIyuIdentity). "
                    + "What an external provider says about a person would then be the session as it stands: signed in "
                    + "without a local account, with whatever claims the provider sent — the permission claims included. "
                    + "Give the handler a scheme of its own (AddCookie(\"external\") and options.SignInScheme = \"external\") "
                    + "and open the session after resolving the account. To use the provider's identity as the session "
                    + $"deliberately, set IdentityTokenOptions.{nameof(IdentityTokenOptions.AllowExternalIdentityAsSession)}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The options type of a remote handler (<c>RemoteAuthenticationHandler&lt;TOptions&gt;</c>), or <c>null</c>.</summary>
    private static Type? RemoteOptionsType(Type handlerType)
    {
        for (var type = handlerType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RemoteAuthenticationHandler<>))
                return type.GetGenericArguments()[0];
        }
        return null;
    }
}
