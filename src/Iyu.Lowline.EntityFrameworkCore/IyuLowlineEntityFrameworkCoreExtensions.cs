using Iyu.Core.Lowline;
using Lowline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Lowline.EntityFrameworkCore;

/// <summary>Turns Lowline on for a host that keeps its records in its own Entity Framework tables.</summary>
public static class IyuLowlineEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Runs the Lowline engine for the forms <paramref name="configure"/> defines, keeping its settled records and
    /// thresholds in the application's own tables.
    /// </summary>
    /// <typeparam name="TContext">The application's <c>DbContext</c>, which holds both tables.</typeparam>
    /// <typeparam name="TRecord">The entity of the table of settled records.</typeparam>
    /// <typeparam name="TTuning">The entity of the table of chosen thresholds.</typeparam>
    /// <remarks>
    /// Registers <see cref="EFLowlineStore{TContext, TRecord, TTuning}"/> as the <see cref="ILowlineStore"/> and the
    /// <see cref="ILowlineRecordIndex"/>, then everything <see cref="IyuLowlineServiceCollectionExtensions.AddIyuLowline"/>
    /// registers. A host that keeps its records some other way calls that overload and registers its own two ports.
    /// </remarks>
    public static IServiceCollection AddIyuLowline<TContext, TRecord, TTuning>(
        this IServiceCollection services,
        Action<IyuLowlineOptions> configure)
        where TContext : DbContext
        where TRecord : class, ILowlineRecordRow, new()
        where TTuning : class, ILowlineTuningRow, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<EFLowlineStore<TContext, TRecord, TTuning>>();
        services.AddSingleton<ILowlineStore>(provider => provider.GetRequiredService<EFLowlineStore<TContext, TRecord, TTuning>>());
        services.AddSingleton<ILowlineRecordIndex>(provider => provider.GetRequiredService<EFLowlineStore<TContext, TRecord, TTuning>>());
        return services.AddIyuLowline(configure);
    }
}
