using Iyu.VaultAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.VaultAI;

public sealed class AddVaultAIReportsGateTests
{
    private static IServiceCollection Build(params (string, string?)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(x => x.Item1, x => x.Item2))
            .Build();
        var services = new ServiceCollection();
        services.AddVaultAIReports(config);
        return services;
    }

    [Fact]
    public void Url_present_registers_client_and_hosted_service()
    {
        var services = Build(
            ("VaultAI:Url", "https://vault.example.test"),
            ("VaultAI:Token", "t"));

        Assert.Contains(services, d => d.ServiceType == typeof(IVaultAIClient));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ReportSchedulerService));
    }

    [Fact]
    public void Url_absent_registers_nothing()
    {
        var services = Build(("VaultAI:Token", "t")); // Url 없음

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IVaultAIClient));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(ReportSchedulerService));
    }
}
