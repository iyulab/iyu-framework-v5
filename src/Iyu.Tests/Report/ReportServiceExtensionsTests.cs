using DocuChef;
using Iyu.Report;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Report;

public class ReportServiceExtensionsTests
{
    [Fact]
    public void AddIyuReport_registers_Chef_as_scoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIyuReport();

        using var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var chefA = scope1.ServiceProvider.GetRequiredService<Chef>();
        var chefB = scope1.ServiceProvider.GetRequiredService<Chef>();
        var chefC = scope2.ServiceProvider.GetRequiredService<Chef>();

        Assert.NotNull(chefA);
        Assert.Same(chefA, chefB);
        Assert.NotSame(chefA, chefC);
    }
}
