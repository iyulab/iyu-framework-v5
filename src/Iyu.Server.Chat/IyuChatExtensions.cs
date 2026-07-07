using BareChat;
using BareChat.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iyu.Server.Chat;

/// <summary>
/// iyu-framework 어댑터: <c>Chat:Enabled</c> 플래그로 BareChat 채팅을 켜고 끈다.
/// 비활성 시 두 확장 모두 완전 no-op이며 라우트/서비스를 등록하지 않는다.
/// </summary>
public static class IyuChatExtensions
{
    /// <summary>활성화 플래그를 읽는 구성 섹션 이름.</summary>
    public const string SectionName = "Chat";

    /// <summary>
    /// <c>Chat:Enabled == true</c>일 때만 BareChat을 등록한다. <c>Chat</c> 섹션은 그대로
    /// <c>BareChatOptions</c>에 바인딩되어 BareChat의 모든 옵션을 노출한다.
    /// 충돌 시 <c>Chat</c> 섹션의 값이 라이브러리의 기본 <c>BareChat</c> 섹션 값을 덮어쓴다.
    /// </summary>
    public static IServiceCollection AddIyuChat(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.GetValue<bool>("Enabled"))
            return services;

        services.AddBareChat(options => section.Bind(options));

        // iyu는 ClaimTypes.Name에 로그인 ID, GivenName에 사람 이름을 싣는다. BareChat 기본
        // provider는 표시명을 로그인 ID로 노출하므로, 표시명을 사람 이름으로 매핑하는 어댑터로 교체.
        services.Replace(ServiceDescriptor.Singleton<IChatAuthProvider, IyuChatAuthProvider>());
        return services;
    }

    /// <summary>
    /// <c>Chat:Enabled == true</c>일 때만 BareChat 미들웨어/엔드포인트를 마운트한다.
    /// 반드시 호스트 인증(<c>UseAuthentication/UseAuthorization</c>) 뒤에 호출한다.
    /// </summary>
    public static WebApplication UseIyuChat(this WebApplication app)
    {
        if (!app.Configuration.GetSection(SectionName).GetValue<bool>("Enabled"))
            return app;

        app.UseBareChat();
        return app;
    }
}
