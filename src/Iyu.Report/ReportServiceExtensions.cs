using DocuChef;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iyu.Report;

public static class ReportServiceExtensions
{
    public static IServiceCollection AddIyuReport(this IServiceCollection services)
    {
        services.AddScoped(sp => new Chef(new RecipeOptions
        {
            LoggerFactory = sp.GetRequiredService<ILoggerFactory>()
        }));

        return services;
    }
}
