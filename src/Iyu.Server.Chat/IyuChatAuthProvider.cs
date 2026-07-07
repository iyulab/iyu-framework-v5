using System.Security.Claims;
using System.Threading.Tasks;
using BareChat;
using BareChat.Core.Domain;
using Microsoft.AspNetCore.Http;

namespace Iyu.Server.Chat;

/// <summary>
/// iyu 클레임 규약을 BareChat <see cref="ChatUserContext"/>로 투영하는 어댑터.
/// </summary>
/// <remarks>
/// iyu(<c>UserAuthClient.GetClaims</c>)는 <see cref="ClaimTypes.Name"/>에 로그인 ID(UserID)를,
/// <see cref="ClaimTypes.GivenName"/>에 사람이 읽는 이름(<c>Account.Name</c> 등)을 싣는다.
/// BareChat 기본 provider는 표시명을 <c>Identity.Name</c>(=Name=로그인 ID)에서 읽으므로 채팅에
/// 로그인 ID가 노출된다. 이 provider는 표시명을 GivenName(사람 이름) 우선으로 매핑하고, 없으면
/// 로그인 ID로 폴백한다. 사용자 식별자는 BareChat 표준대로 <see cref="ClaimTypes.NameIdentifier"/>.
/// </remarks>
internal sealed class IyuChatAuthProvider : IChatAuthProvider
{
    public Task<ChatUserContext> ResolveUserAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return Task.FromResult(ChatUserContext.Anonymous);

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? user.Identity.Name
                     ?? "unknown";

        var displayName = user.FindFirst(ClaimTypes.GivenName)?.Value
                          ?? user.Identity.Name
                          ?? userId;

        var avatar = user.FindFirst("avatar")?.Value ?? string.Empty;

        return Task.FromResult(new ChatUserContext
        {
            UserId = userId,
            DisplayName = displayName,
            AvatarUrl = avatar,
            IsAuthenticated = true
        });
    }
}
