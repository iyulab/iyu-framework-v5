using Lowline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>Turns Lowline on for a host.</summary>
public static class IyuLowlineServiceCollectionExtensions
{
    /// <summary>
    /// Runs the Lowline engine for the forms <paramref name="configure"/> defines, keeping its settled records and
    /// thresholds in the application's own tables.
    /// </summary>
    /// <typeparam name="TContext">The application's <c>DbContext</c>, which holds both tables.</typeparam>
    /// <typeparam name="TRecord">The entity of the table of settled records.</typeparam>
    /// <typeparam name="TTuning">The entity of the table of chosen thresholds.</typeparam>
    /// <remarks>
    /// Nothing is registered until this is called, and a host that calls it without defining a form does not start.
    /// Registers <see cref="ILowlineStore"/> and the engine's <see cref="LowlineForms"/> as singletons; a
    /// <see cref="ILowlineModel"/> already in the container fills forms from what a person wrote.
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

        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuLowlineOptions>, IyuLowlineOptionsValidator>();
        services.AddOptions<IyuLowlineOptions>().ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddDistributedMemoryCache();
        services.TryAddSingleton<ILowlineRoll, HmacLowlineRoll>();
        services.AddSingleton<IyuLowlineRecheck>();
        services.AddSingleton<EfLowlineStore<TContext, TRecord, TTuning>>();
        services.AddSingleton<ILowlineStore>(provider => provider.GetRequiredService<EfLowlineStore<TContext, TRecord, TTuning>>());
        services.AddSingleton<ILowlineRecordIndex>(provider => provider.GetRequiredService<EfLowlineStore<TContext, TRecord, TTuning>>());
        services.AddScoped<IyuLowlineSettleService>();
        services.AddScoped<IyuLowlineProposeService>();
        services.AddSingleton<IyuLowlineRecoveryService>();
        services.AddHostedService<IyuLowlineRecoveryWorker>();
        services.AddSingleton(provider =>
        {
            var engine = provider.GetRequiredService<IOptions<IyuLowlineOptions>>().Value.Engine;
            engine.UseStore(provider.GetRequiredService<ILowlineStore>());
            if (engine.Model is null && provider.GetService<ILowlineModel>() is { } model)
                engine.UseModel(model);
            return engine.Build(provider.GetService<TimeProvider>() ?? TimeProvider.System);
        });
        return services;
    }
}
