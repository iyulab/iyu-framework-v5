using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Receiving logout notices from OpenID Connect providers — OpenID Connect Back-Channel Logout 1.0.
/// </summary>
public static class IyuBackChannelLogoutExtensions
{
    /// <summary>
    /// Where a provider posts its logout notices for the handler registered as <c>{provider}</c>:
    /// <c>POST /api/auth/external/{provider}/backchannel-logout</c>. Register the absolute form of this with the
    /// provider as the client's <c>backchannel_logout_uri</c>.
    /// </summary>
    public const string Path = "/api/auth/external/{provider}/backchannel-logout";

    /// <summary>
    /// Lets the application's sessions end when a provider says the session they were opened from is over: accepts the
    /// provider's logout notice at <see cref="MapIyuBackChannelLogout"/> and ends those sessions
    /// (<see cref="ExternalSessionService"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calls <see cref="IyuIdentityServiceCollectionExtensions.AddIyuExternalSessionEnds"/>, so the application
    /// registers an <see cref="IExternalSessionEndStore"/> (a host without one is refused at startup).
    /// </para>
    /// <para>
    /// A notice is checked against the OpenID Connect handler it names — the provider's issuer and signing keys, the
    /// client id — so nothing about the provider is configured again here.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">How notices are accepted. Optional.</param>
    public static IServiceCollection AddIyuBackChannelLogout(
        this IServiceCollection services, Action<BackChannelLogoutOptions>? configure = null)
    {
        var options = new BackChannelLogoutOptions();
        configure?.Invoke(options);
        if (options.MaxAgeWithoutExpiration <= TimeSpan.Zero)
            throw new ArgumentException(
                $"{nameof(BackChannelLogoutOptions)}.{nameof(BackChannelLogoutOptions.MaxAgeWithoutExpiration)} must be "
                + $"positive. To refuse logout tokens without an expiry, set {nameof(BackChannelLogoutOptions.RequireExpiration)}.",
                nameof(configure));

        services.AddIyuExternalSessionEnds();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BackChannelLogoutTokenValidator>();
        return services;
    }

    /// <summary>
    /// Maps <c>POST /api/auth/external/{provider}/backchannel-logout</c> (<see cref="Path"/>), where the provider
    /// behind the OpenID Connect handler <c>{provider}</c> posts its logout token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anonymous, and not a browser's request: the provider calls it server to server, and the signed token is what
    /// authenticates it. 200 with no body when the token is accepted — whether or not any session here was opened from
    /// the session it names; 400 with <c>{"error":"invalid_request","error_description":…}</c> when it is refused;
    /// 404 when <c>{provider}</c> is not an OpenID Connect handler. Responses are <c>Cache-Control: no-store</c>.
    /// </para>
    /// <para>
    /// A token must name the session it ends (<c>sid</c>). One that names only a person is refused: sessions here are
    /// not kept by the provider's subject.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapIyuBackChannelLogout(this IEndpointRouteBuilder app)
    {
        if (app.ServiceProvider.GetService<IServiceProviderIsService>() is { } registered
            && !registered.IsService(typeof(BackChannelLogoutTokenValidator)))
            throw new InvalidOperationException(
                $"{nameof(MapIyuBackChannelLogout)} needs services.{nameof(AddIyuBackChannelLogout)}() — nothing would "
                + "check the provider's logout token.");

        app.MapPost(Path, (string provider, HttpContext http, IAuthenticationSchemeProvider schemes,
                    IOptionsMonitor<OpenIdConnectOptions> handlers, BackChannelLogoutTokenValidator validator,
                    ExternalSessionService sessions)
                => BackChannelLogoutHandler.ReceiveAsync(provider, http, schemes, handlers, validator, sessions))
            .AllowAnonymous()
            .DisableAntiforgery();
        return app;
    }
}
