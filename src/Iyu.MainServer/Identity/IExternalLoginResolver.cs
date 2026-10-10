using System.Security.Claims;

namespace Iyu.MainServer.Identity;

/// <summary>
/// What an external provider said about the person who just signed in there.
/// </summary>
/// <param name="Provider">The authentication scheme of the handler that received it (<c>"oidc"</c>, say).</param>
/// <param name="ProviderKey">
/// The person's identifier at that provider — its <c>sub</c>. Unique only together with <paramref name="Provider"/>:
/// two providers may hand out the same value for different people.
/// </param>
/// <param name="Principal">Everything the provider sent, for an application that maps more than the identifier.</param>
public sealed record ExternalLogin(string Provider, string ProviderKey, ClaimsPrincipal Principal);

/// <summary>
/// Decides which local account an external sign-in is — the one step of signing in through an external provider that
/// is the application's. Implemented by the consuming app; asked by the completion endpoint
/// (<see cref="IdentityEndpointRouteExtensions.MapIyuExternalSignIn"/>).
/// </summary>
/// <remarks>
/// <para>
/// Answer the subject of the local account — the same value <see cref="UserSessionService.SignInAsync"/> takes — and
/// the framework opens that account's session from <see cref="IUserClaimsSource"/>. Nothing the provider sent is
/// carried into it.
/// </para>
/// <para>
/// Whether a first-seen external identity gets an account (provisioning) or must already be linked to one, and which
/// of its claims fill which local fields, is a policy of each deployment — it lives here. Answer <c>null</c> to refuse:
/// the person is not signed in.
/// </para>
/// </remarks>
public interface IExternalLoginResolver
{
    Task<string?> ResolveSubjectAsync(ExternalLogin login, CancellationToken ct);
}
