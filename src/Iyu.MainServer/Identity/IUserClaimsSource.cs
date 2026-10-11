using System.Security.Claims;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Answers, for a subject, the claims that person's session carries in this application — or that they
/// get none. Implemented by the consuming app; asked whenever a session is opened for a person, whichever
/// form it takes: the session cookie (<see cref="UserSessionService"/>, at sign-in and again while it lives) or an
/// access token (<see cref="UserAccessTokenService"/>; <see cref="UserTokenService"/>, at sign-in and again at every
/// refresh).
/// </summary>
/// <remarks>
/// <para>
/// One source for both forms is the point: the person a cookie describes and the person a token
/// describes are assembled from the same answer, so they cannot drift apart — and a permission claim
/// the policies need cannot be left out of one of them by hand.
/// </para>
/// <para>
/// Answer the permission claims (<see cref="IyuIdentityClaims.Permission"/>) and whatever else the
/// application reads from a signed-in person. The subject's identifier is not the source's to add:
/// the framework writes it from the subject it asked about (see <see cref="IyuIdentityClaims.FindSubject"/>),
/// and an identifier claim in the answer that names someone else is an error.
/// </para>
/// <para>
/// Answer <c>null</c> to refuse — no account, or one that is deactivated. Nothing is signed in; a
/// refresh fails and its token family is revoked.
/// </para>
/// <para>
/// With service clients on (<see cref="IyuIdentityServiceCollectionExtensions.AddIyuServiceClients"/>) the source is
/// also asked about each client's owner — by the owner's id as a GUID string — when a client is issued, re-scoped,
/// obtains a token, and while its tokens are checked: a client never carries more than its owner holds now, and an
/// owner the source refuses leaves their clients nothing.
/// </para>
/// </remarks>
public interface IUserClaimsSource
{
    Task<IReadOnlyCollection<Claim>?> GetClaimsAsync(string subject, CancellationToken ct);
}
