using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Opens the session cookie for a person the application has authenticated — the cookie counterpart of
/// <see cref="UserTokenService.IssueAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The application still decides who the person is (a password it checked, an external provider's
/// identity it matched to an account) and then calls <see cref="SignInAsync"/> with that account's
/// subject. The session is assembled here: the claims <see cref="IUserClaimsSource"/> answers for the
/// subject, and the subject's identifier. An application that builds the principal by hand has to
/// remember every permission claim the policies need; this is the step that does not forget.
/// </para>
/// <para>
/// Nothing here checks a credential, and nothing from the caller's current identity is carried over —
/// the session is the source's answer and nothing else.
/// </para>
/// </remarks>
public sealed class UserSessionService
{
    private readonly IUserClaimsSource _claims;
    private readonly ILogger<UserSessionService> _log;

    public UserSessionService(IUserClaimsSource claims, ILogger<UserSessionService> log)
    {
        _claims = claims; _log = log;
    }

    /// <summary>
    /// Signs <paramref name="subject"/> into the session cookie with the claims the source answers.
    /// </summary>
    /// <param name="http">The request being answered; the cookie is written to its response.</param>
    /// <param name="subject">The account the application authenticated.</param>
    /// <param name="properties">Cookie properties — persistence, an explicit expiry. Optional.</param>
    /// <param name="ct">Cancels the question to the claims source.</param>
    /// <returns>
    /// <c>false</c> when the source refuses the subject: nothing is signed in, and a session the request
    /// already had is left as it was.
    /// </returns>
    public async Task<bool> SignInAsync(
        HttpContext http, string subject, AuthenticationProperties? properties = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var principal = await CreatePrincipalAsync(subject, ct);
        if (principal is null)
        {
            _log.LogWarning("sign-in refused for {Subject}: the claims source declined the subject", subject);
            return false;
        }

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        return true;
    }

    /// <summary>
    /// The principal a session for <paramref name="subject"/> carries, or <c>null</c> when the source
    /// refuses the subject.
    /// </summary>
    internal async Task<ClaimsPrincipal?> CreatePrincipalAsync(string subject, CancellationToken ct)
    {
        var answered = await _claims.GetClaimsAsync(subject, ct);
        if (answered is null) return null;

        return new ClaimsPrincipal(new ClaimsIdentity(
            IyuIdentityClaims.ForSubject(answered, subject, ClaimTypes.NameIdentifier),
            CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
