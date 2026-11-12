using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Iyu.ML;

/// <summary>Turns the ML layer on for a host.</summary>
public static class IyuMLServiceCollectionExtensions
{
    /// <summary>
    /// Offers the datasets <paramref name="configure"/> declares for training. The layer is explicit: nothing is registered
    /// until this is called, and a host that calls it without a dataset, without an <see cref="IMLDatasetSource"/> that
    /// serves every declared dataset, or without saying how read permissions apply (an <see cref="IMLReadGuard"/>, or
    /// <see cref="IyuMLOptions.NoReadPolicies"/>) does not start. The message names what is missing, never a value.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Declares the datasets.</param>
    public static IServiceCollection AddIyuML(this IServiceCollection services, Action<IyuMLOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IyuMLRegistration>();
        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuMLOptions>, IyuMLOptionsValidator>();
        services.AddOptions<IyuMLOptions>().ValidateOnStart();
        services.AddHostedService<IyuMLPortsCheck>();
        return services;
    }
}

/// <summary>
/// Fails the host's start when what the declared datasets need was never registered. It works in the constructor because that
/// is the first moment the host asks for anything of the layer, and a message here names the missing piece where an error from
/// deep inside a later request would not.
/// </summary>
internal sealed class IyuMLPortsCheck : IHostedService
{
    public IyuMLPortsCheck(IServiceProvider services, IOptions<IyuMLOptions> options)
    {
        var declared = options.Value;
        var problems = new List<string>();

        var source = services.GetService(typeof(IMLDatasetSource)) as IMLDatasetSource;
        if (source is null)
        {
            problems.Add("register an IMLDatasetSource (the Entity Framework adapter provides one over the application's DbContext)");
        }
        else
        {
            var served = source.Datasets.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dataset in declared.Datasets.Where(d => !served.Contains(d.Name)))
                problems.Add($"serve the declared dataset '{dataset.Name}' from the registered IMLDatasetSource");
        }

        // A guard may depend on scoped services, so it is looked for in a scope, not on the root.
        using var scope = services.CreateScope();
        var guards = scope.ServiceProvider.GetServices<IMLReadGuard>().Count();
        if (guards > 1)
            problems.Add($"register one IMLReadGuard, not {guards}: a host answers for its read policies in one place");
        if (guards == 0 && !declared.ReadPoliciesNotUsed)
            problems.Add("say how read permissions apply: register an IMLReadGuard, or call NoReadPolicies() when the host has no read policies");
        if (guards > 0 && declared.ReadPoliciesNotUsed)
            problems.Add("choose between an IMLReadGuard and NoReadPolicies(): the host registered a guard and also said it has no read policies");

        if (problems.Count == 0) return;
        throw new InvalidOperationException("AddIyuML needs the host to " + string.Join("; and to ", problems) + ".");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Present when <c>AddIyuML</c> was called: what an adapter asks, so it never serves rows for a layer that was not turned on.</summary>
internal sealed class IyuMLRegistration;
