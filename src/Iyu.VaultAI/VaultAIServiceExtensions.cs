using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAI;

public static class VaultAIServiceExtensions
{
    public static IServiceCollection AddVaultAIReports(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("VaultAI");
        services.Configure<VaultAISettings>(section);

        // "VaultAI" 섹션 미구성(Url 부재) 시 전체 기능 비활성 —
        // VaultAIClient 생성자가 절대 URI를 요구하므로 등록 자체를 생략한다.
        if (string.IsNullOrWhiteSpace(section[nameof(VaultAISettings.Url)]))
            return services;

        // The module's routes are held to an authorization policy (VaultAISettings.AuthorizationPolicy) by
        // UseVaultAIReports; the policy services have to be there even when the host registers none of its own.
        services.AddAuthorization();

        // A limit that is not greater than zero would fail every call; say so at start, naming the key, not the value.
        services.AddOptions<VaultAISettings>()
            .Validate(s => s.RequestTimeout > TimeSpan.Zero, "VaultAI:RequestTimeout has to be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<IVaultAIClient, VaultAIClient>();

        // The widget's token call (MapVaultAIEmbed) — same certificate rule as the client, no state of its own. The
        // deadline is the module's own (VaultAI:EmbedTokenTimeout); the client's default hundred seconds would otherwise
        // cut in first whenever a longer one is configured.
        services.AddHttpClient(VaultAIEmbedExtensions.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider =>
                VaultAIClient.CreateHandler(provider.GetRequiredService<IOptions<VaultAISettings>>().Value));
        services.AddHostedService<ReportSchedulerService>();
        services.AddDirectoryBrowser();   // {BasePath}-files 디렉터리 브라우징 지원
        return services;
    }
}
