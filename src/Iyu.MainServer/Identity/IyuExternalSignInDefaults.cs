namespace Iyu.MainServer.Identity;

/// <summary>Names used by external sign-in (<see cref="IyuIdentityServiceCollectionExtensions.AddIyuExternalSignIn"/>).</summary>
public static class IyuExternalSignInDefaults
{
    /// <summary>
    /// The cookie scheme that holds what an external provider said about a person until the completion endpoint turns
    /// it into the application's session. Nothing the application serves accepts it.
    /// </summary>
    public const string AuthenticationScheme = "Iyu.External";

    /// <summary>Where a sign-in through <c>{provider}</c> starts: <c>GET /api/auth/external/{provider}?returnUrl=…</c>.</summary>
    public const string StartPath = "/api/auth/external";

    /// <summary>Where the provider's handler sends the browser back to once it has accepted the provider's answer.</summary>
    public const string CompletePath = "/api/auth/external/complete";
}
