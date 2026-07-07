using Iyu.VaultAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.VaultAi;

public sealed class AddVaultAiReportsGateTests
{
    private static IServiceCollection Build(params (string, string?)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(x => x.Item1, x => x.Item2))
            .Build();
        var services = new ServiceCollection();
        services.AddVaultAiReports(config);
        return services;
    }

    [Fact]
    public void Url_present_registers_client_and_hosted_service()
    {
        var services = Build(
            ("VaultAi:Url", "https://vault.example.test"),
            ("VaultAi:Token", "t"));

        Assert.Contains(services, d => d.ServiceType == typeof(IVaultAiClient));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ReportSchedulerService));
    }

    [Fact]
    public void Url_absent_registers_nothing()
    {
        var services = Build(("VaultAi:Token", "t")); // Url 없음

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IVaultAiClient));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ReportSchedulerService));
    }
}
