using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Iyu.Ml;

/// <summary>Turns the ML layer on for a host.</summary>
public static class IyuMlServiceCollectionExtensions
{
    /// <summary>
    /// Offers the datasets <paramref name="configure"/> declares for training. The layer is explicit: nothing is registered
    /// until this is called, and a host that calls it without a dataset, without an <see cref="IMlDatasetSource"/> that
    /// serves every declared dataset, or without saying how read permissions apply (an <see cref="IMlReadGuard"/>, or
    /// <see cref="IyuMlOptions.NoReadPolicies"/>) does not start. The message names what is missing, never a value.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Declares the datasets.</param>
    public static IServiceCollection AddIyuMl(this IServiceCollection services, Action<IyuMlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IyuMlRegistration>();
        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuMlOptions>, IyuMlOptionsValidator>();
        services.AddOptions<IyuMlOptions>().ValidateOnStart();
        services.AddHostedService<IyuMlPortsCheck>();
        return services;
    }
}

/// <summary>
/// Fails the host's start when what the declared datasets need was never registered. It works in the constructor because that
/// is the first moment the host asks for anything of the layer, and a message here names the missing piece where an error from
/// deep inside a later request would not.
/// </summary>
internal sealed class IyuMlPortsCheck : IHostedService
{
    public IyuMlPortsCheck(IServiceProvider services, IOptions<IyuMlOptions> options)
    {
        var declared = options.Value;
        var problems = new List<string>();

        var source = services.GetService(typeof(IMlDatasetSource)) as IMlDatasetSource;
        if (source is null)
        {
            problems.Add("register an IMlDatasetSource (the Entity Framework adapter provides one over the application's DbContext)");
        }
        else
        {
            var served = source.Datasets.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dataset in declared.Datasets.Where(d => !served.Contains(d.Name)))
                problems.Add($"serve the declared dataset '{dataset.Name}' from the registered IMlDatasetSource");
        }

        // A guard may depend on scoped services, so it is looked for in a scope, not on the root.
        using var scope = services.CreateScope();
        var guards = scope.ServiceProvider.GetServices<IMlReadGuard>().Count();
        if (guards > 1)
            problems.Add($"register one IMlReadGuard, not {guards}: a host answers for its read policies in one place");
        if (guards == 0 && !declared.ReadPoliciesNotUsed)
            problems.Add("say how read permissions apply: register an IMlReadGuard, or call NoReadPolicies() when the host has no read policies");
        if (guards > 0 && declared.ReadPoliciesNotUsed)
            problems.Add("choose between an IMlReadGuard and NoReadPolicies(): the host registered a guard and also said it has no read policies");

        if (problems.Count == 0) return;
        throw new InvalidOperationException("AddIyuMl needs the host to " + string.Join("; and to ", problems) + ".");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Present when <c>AddIyuMl</c> was called: what an adapter asks, so it never serves rows for a layer that was not turned on.</summary>
internal sealed class IyuMlRegistration;
