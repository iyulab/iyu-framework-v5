using Lowline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>Turns Lowline on for a host.</summary>
public static class IyuLowlineServiceCollectionExtensions
{
    /// <summary>
    /// Runs the Lowline engine for the forms <paramref name="configure"/> defines, over the two ports the host registers
    /// for keeping its records: the engine's <see cref="ILowlineStore"/> and the <see cref="ILowlineRecordIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This overload assumes no storage technology. A host that keeps its records in its own Entity Framework tables
    /// references <c>Iyu.Lowline.EntityFrameworkCore</c> and calls its overload, which registers both ports and then this.
    /// </para>
    /// <para>
    /// Nothing is registered until this is called, and a host that calls it without defining a form, or without
    /// registering both ports, does not start (the message names what is missing, never a value). Registers the engine's
    /// <see cref="LowlineForms"/> as a singleton; a <see cref="ILowlineModel"/> already in the container fills forms
    /// from what a person wrote.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddIyuLowline(
        this IServiceCollection services,
        Action<IyuLowlineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuLowlineOptions>, IyuLowlineOptionsValidator>();
        services.AddOptions<IyuLowlineOptions>().ValidateOnStart();
        // The passes below ask for the record index in their constructors, and the container refuses to build them at all
        // when nothing is registered for it — with an error that names the pass, not the port the host forgot. A stand-in
        // that a host's own registration replaces (whichever order the two are written in) lets the check below speak.
        services.TryAddSingleton<ILowlineRecordIndex, MissingLowlineRecordIndex>();
        services.AddHostedService<IyuLowlinePortsCheck>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddDistributedMemoryCache();
        services.TryAddSingleton<ILowlineRoll, HmacLowlineRoll>();
        services.AddSingleton<IyuLowlineRecheck>();
        services.AddScoped<IyuLowlineSettleService>();
        services.AddScoped<IyuLowlineProposeService>();
        services.AddSingleton<IyuLowlineRecoveryService>();
        services.AddHostedService<IyuLowlineRecoveryWorker>();
        services.AddSingleton<IyuLowlineHistoryService>();
        services.AddHostedService<IyuLowlineHistoryWorker>();
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

/// <summary>
/// Fails the host's start when a port the services read records through was never registered — the alternative is a
/// dependency-injection error from deep inside a background worker that does not say which of the two was forgotten.
/// It does its work in the constructor because that is the first moment the host asks for anything of Lowline.
/// </summary>
internal sealed class IyuLowlinePortsCheck : IHostedService
{
    public IyuLowlinePortsCheck(IServiceProvider services)
    {
        var missing = new List<string>();
        if (services.GetService<ILowlineStore>() is null) missing.Add(nameof(ILowlineStore));
        if (services.GetService<ILowlineRecordIndex>() is null or MissingLowlineRecordIndex) missing.Add(nameof(ILowlineRecordIndex));
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            $"AddIyuLowline needs the host to register {string.Join(" and ", missing)}. A host that keeps its records in " +
            "its own Entity Framework tables references Iyu.Lowline.EntityFrameworkCore and calls its AddIyuLowline<TContext, TRecord, TTuning>; " +
            "any other host registers its own implementation of each port.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>What stands in for the record index until the host registers its own; every call says so.</summary>
internal sealed class MissingLowlineRecordIndex : ILowlineRecordIndex
{
    private static InvalidOperationException Missing() =>
        new("No ILowlineRecordIndex is registered. Register one, or reference Iyu.Lowline.EntityFrameworkCore and call its AddIyuLowline<TContext, TRecord, TTuning>.");

    public Task<IReadOnlyList<LowlineScope>> ListScopesAsync(CancellationToken cancellationToken = default) => throw Missing();

    public Task<IReadOnlyList<string>> ListRecordIdsAsync(LowlineScope scope, CancellationToken cancellationToken = default) => throw Missing();
}
