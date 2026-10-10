using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Opens the session cookie for a person the application has authenticated — the cookie counterpart of
/// <see cref="UserTokenService.IssueAsync"/> — and keeps it true to the account for as long as it lives.
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
/// <para>
/// <b>The source is asked again while the session lives</b>, every
/// <see cref="IdentityTokenOptions.SessionRevalidationInterval"/>: a person deactivated since is signed
/// out, and a narrowed permission set replaces the one in the cookie — what a refresh does for an access
/// token. A session cookie this service did not open (a principal an endpoint built and signed in by
/// hand) is re-assembled from the source the first time it is presented, by the identifier it carries;
/// one that carries none is signed out.
/// </para>
/// </remarks>
public sealed class UserSessionService
{
    /// <summary>Where a session cookie records when the source last answered for it.</summary>
    private const string ValidatedAtKey = ".iyu.validated";

    /// <summary>Where a session opened through an external provider records which one (its authentication scheme).</summary>
    internal const string ProviderKey = ".iyu.provider";

    private readonly IUserClaimsSource _claims;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserSessionService> _log;

    public UserSessionService(IUserClaimsSource claims, TimeProvider clock, ILogger<UserSessionService> log)
    {
        _claims = claims; _clock = clock; _log = log;
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

        properties ??= new AuthenticationProperties();
        MarkValidated(properties, _clock.GetUtcNow());
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        return true;
    }

    /// <summary>
    /// Ends the session cookie — and, for a session opened through an external provider whose handler can sign out
    /// (OpenID Connect's RP-initiated logout), the person's session at that provider too.
    /// </summary>
    /// <param name="http">The request being answered.</param>
    /// <param name="properties">
    /// Passed to the provider's sign-out: its <see cref="AuthenticationProperties.RedirectUri"/> is where the provider
    /// sends the browser back to afterwards. Optional.
    /// </param>
    /// <returns>
    /// <c>true</c> when the response now sends the browser to the provider — the caller writes no response of its own;
    /// <c>false</c> when only the application's session was ended.
    /// </returns>
    public async Task<bool> SignOutAsync(HttpContext http, AuthenticationProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        var session = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var provider = session.Properties?.Items.TryGetValue(ProviderKey, out var name) == true ? name : null;
        var schemes = http.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        var signOutAtProvider = provider is not null
                                && await schemes.GetSchemeAsync(provider) is { } scheme
                                && typeof(IAuthenticationSignOutHandler).IsAssignableFrom(scheme.HandlerType);

        // The application's session ends first, whatever the provider's handler then does. That handler still finds the
        // identity token it names the session with (id_token_hint): it reads the cookie the request carried.
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (signOutAtProvider) await http.SignOutAsync(provider, properties ?? new AuthenticationProperties());
        return signOutAtProvider;
    }

    /// <summary>
    /// Whether the session cookie being validated is due for the source to be asked again. The common
    /// case — it is not — is decided here, from the cookie alone, before anything scoped is resolved.
    /// </summary>
    internal static bool IsRevalidationDue(CookieValidatePrincipalContext context, IdentityTokenOptions options)
    {
        if (ValidatedAt(context.Properties) is not { } validatedAt)
        {
            // Not opened by this service. Where the application has said an external provider's identity is its
            // session, that is what such a cookie is, and no account stands behind it to ask about.
            return !options.AllowExternalIdentityAsSession;
        }

        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        return now - validatedAt >= options.SessionRevalidationInterval;
    }

    /// <summary>
    /// Asks the source again for the session being validated: replaces its claims with the answer, or
    /// ends it when the source refuses the subject or the cookie names none.
    /// </summary>
    internal async Task RevalidateAsync(CookieValidatePrincipalContext context)
    {
        var subject = context.Principal is null ? null : IyuIdentityClaims.FindSubject(context.Principal);
        var principal = subject is null
            ? null
            : await CreatePrincipalAsync(subject, context.HttpContext.RequestAborted);

        if (principal is null)
        {
            _log.LogInformation(
                "session ended for {Subject}: {Reason}", subject ?? "(no identifier)",
                subject is null ? "the cookie names no subject" : "the claims source declined the subject");
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var now = _clock.GetUtcNow();
        MarkValidated(context.Properties, now);
        context.ReplacePrincipal(principal);
        context.ShouldRenew = true;
        // Renewal re-issues the cookie for the span ExpiresUtc − IssuedUtc counted from now. Without sliding
        // expiration that must not push the session's end further out than the sign-in set it: with IssuedUtc at
        // now, the span is the time left, and the end stays where it was.
        if (!context.Options.SlidingExpiration) context.Properties.IssuedUtc = now;
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

    private static void MarkValidated(AuthenticationProperties properties, DateTimeOffset at)
        => properties.Items[ValidatedAtKey] = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ValidatedAt(AuthenticationProperties properties)
        => properties.Items.TryGetValue(ValidatedAtKey, out var value)
           && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
}
