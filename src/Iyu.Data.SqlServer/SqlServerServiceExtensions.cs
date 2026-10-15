using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iyu.Data.SqlServer;

public static class SqlServerServiceExtensions
{
    /// <summary>
    /// Registers <see cref="SqlServerSqlStateTranslator"/>, so a SQL Server write failure is classified by its standard
    /// SQLSTATE — a duplicate, a missing reference, a missing value or a value too long gets its own status and code
    /// instead of the neutral conflict. Order relative to <c>AddIyuMainServer</c> does not matter.
    /// </summary>
    public static IServiceCollection AddIyuSqlServerErrors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISqlStateTranslator, SqlServerSqlStateTranslator>());
        return services;
    }
}
