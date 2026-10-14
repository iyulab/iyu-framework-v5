namespace Iyu.MainServer.Identity;

/// <summary>
/// Which accounts are service accounts — accounts a credential is held under rather than people who sign in. The host
/// answers from its own account model (an account that cannot sign in, a marking claim); the framework knows no kind of
/// account.
/// </summary>
/// <remarks>
/// <para>
/// Registering it opens issuance on a service account's behalf to the administrators of
/// <see cref="IdentityEndpointRouteExtensions.MapIyuServiceClientAdministration"/>: an administrator who holds none of a
/// service account's permissions issues, rotates and re-scopes its credentials, and the credential stays under the
/// service account's permissions as they are now — the same ceiling an owner's own credential has.
/// </para>
/// <para>
/// Only for service accounts: acting in a person's name would hand the administrator that person's permissions. Without
/// a registration an administrator issues nothing.
/// </para>
/// <para>
/// What it separates is who may <em>mint</em> a credential, not who may use one: an issue or a rotation answers the
/// secret to the administrator who asked, once — as a cloud provider answers a service account's new key to whoever
/// created it — and a holder of that secret obtains tokens with the service account's permissions. The issuer is
/// recorded (<see cref="ServiceClientSummary.IssuedBy"/>) and each act logged, refusals included, so what an
/// administrator account issued can be found and revoked.
/// </para>
/// </remarks>
public interface IServiceAccounts
{
    /// <summary>Whether <paramref name="accountId"/> is a service account.</summary>
    Task<bool> IsServiceAccountAsync(Guid accountId, CancellationToken ct);
}
