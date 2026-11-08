using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAi;

public static class VaultAiServiceExtensions
{
    public static IServiceCollection AddVaultAiReports(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("VaultAi");
        services.Configure<VaultAiSettings>(section);

        // "VaultAi" 섹션 미구성(Url 부재) 시 전체 기능 비활성 —
        // VaultAiClient 생성자가 절대 URI를 요구하므로 등록 자체를 생략한다.
        if (string.IsNullOrWhiteSpace(section[nameof(VaultAiSettings.Url)]))
            return services;

        // The module's routes are held to an authorization policy (VaultAiSettings.AuthorizationPolicy) by
        // UseVaultAiReports; the policy services have to be there even when the host registers none of its own.
        services.AddAuthorization();

        // A limit that is not greater than zero would fail every call; say so at start, naming the key, not the value.
        services.AddOptions<VaultAiSettings>()
            .Validate(s => s.RequestTimeout > TimeSpan.Zero, "VaultAi:RequestTimeout has to be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<IVaultAiClient, VaultAiClient>();

        // The widget's token call (MapVaultAiEmbed) — same certificate rule as the client, no state of its own. The
        // deadline is the module's own (VaultAi:EmbedTokenTimeout); the client's default hundred seconds would otherwise
        // cut in first whenever a longer one is configured.
        services.AddHttpClient(VaultAiEmbedExtensions.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider =>
                VaultAiClient.CreateHandler(provider.GetRequiredService<IOptions<VaultAiSettings>>().Value));
        services.AddHostedService<ReportSchedulerService>();
        services.AddDirectoryBrowser();   // {BasePath}-files 디렉터리 브라우징 지원
        return services;
    }
}
