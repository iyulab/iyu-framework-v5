using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BoardChat;

/// <summary>
/// A sign-in for a sample: the caller is the user named in the <c>X-Sample-User</c> header, or <c>analyst</c>. <c>finance</c> is the
/// user who may read the protected columns. A real host puts its own authentication here.
/// </summary>
public sealed class SampleUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Sample-User"].FirstOrDefault() is { Length: > 0 } named ? named : "analyst";
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user), new Claim(ClaimTypes.Role, user)], "Sample");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Sample")));
    }
}
