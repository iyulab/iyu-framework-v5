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

/// <summary>
/// Who may do what to a board: analysts and finance make and change boards; anyone signed in (a <c>viewer</c>) may open and ask. A
/// <c>partner</c> may open only the boards whose id starts with <c>shared-</c>; the others are not there for them.
/// </summary>
public sealed class SampleBoardAccess : Iyu.UBoard.IBoardAccess
{
    public ValueTask<bool> AllowsAsync(ClaimsPrincipal user, Iyu.UBoard.BoardAction action, string? boardId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(action == Iyu.UBoard.BoardAction.View
            ? !user.IsInRole("partner") || boardId?.StartsWith("shared-", StringComparison.Ordinal) == true
            : user.IsInRole("analyst") || user.IsInRole("finance"));

    public ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
