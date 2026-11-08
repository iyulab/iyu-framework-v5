using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.Ml.EntityFrameworkCore;

/// <summary>Declares the datasets of a host whose rows live in its own Entity Framework tables.</summary>
/// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
public sealed class MlEntityFrameworkBuilder<TContext> where TContext : DbContext
{
    private readonly List<IEfDatasetPlan> _plans = [];

    internal IReadOnlyList<IEfDatasetPlan> Plans => _plans;

    /// <summary>Declares a dataset read from <typeparamref name="TEntity"/>.</summary>
    /// <param name="name">The dataset's name.</param>
    /// <param name="columns">Names the input columns, the answer column and, optionally, which rows.</param>
    /// <typeparam name="TEntity">An entity of the application's model.</typeparam>
    /// <exception cref="ArgumentException">The name is empty or taken, or the columns do not make a dataset (no input, two answers).</exception>
    public MlEntityFrameworkBuilder<TContext> Dataset<TEntity>(string name, Action<MlDatasetBuilder<TEntity>> columns)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        if (_plans.Any(p => string.Equals(p.Definition.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A dataset named '{name}' is already declared (names are compared ignoring case).", nameof(name));

        var builder = new MlDatasetBuilder<TEntity>();
        columns(builder);
        _plans.Add(builder.Build(name));
        return this;
    }
}

/// <summary>Registers the Entity Framework dataset source.</summary>
public static class IyuMlEntityFrameworkExtensions
{
    /// <summary>
    /// Reads the datasets <paramref name="configure"/> declares from the application's <typeparamref name="TContext"/>, and
    /// declares them to <see cref="IyuMlServiceCollectionExtensions.AddIyuMl"/> (call that too — it is what turns the layer
    /// on and checks the host). One context per host: a second source is refused.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Declares the datasets.</param>
    /// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
    /// <remarks>
    /// At start the host also checks that every named entity and property is part of the context's model and, when an
    /// <see cref="IMlReadGuard"/> is registered, that it allows every column and every property the filters read. A column
    /// the guard refuses stops the host from starting; nothing is silently dropped.
    /// </remarks>
    public static IServiceCollection AddIyuMlEntityFramework<TContext>(
        this IServiceCollection services, Action<MlEntityFrameworkBuilder<TContext>> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(d => d.ServiceType == typeof(IMlDatasetSource)))
            throw new InvalidOperationException("A dataset source is already registered; a host reads its datasets from one source.");

        var builder = new MlEntityFrameworkBuilder<TContext>();
        configure(builder);
        if (builder.Plans.Count == 0)
            throw new ArgumentException("Declare at least one dataset.", nameof(configure));
        var plans = builder.Plans;

        foreach (var plan in plans)
        {
            var definition = plan.Definition;
            services.Configure<IyuMlOptions>(o => o.Dataset(definition));
        }
        services.AddSingleton<IMlDatasetSource>(provider => new EfMlDatasetSource<TContext>(provider.GetRequiredService<IServiceScopeFactory>(), plans));
        services.AddHostedService(provider => new EfMlDeclarationCheck<TContext>(provider.GetRequiredService<IServiceScopeFactory>(), plans));
        return services;
    }
}

/// <summary>
/// Fails the host's start when a declared dataset cannot be read as declared: an entity or property outside the context's model,
/// or a column or filter property the read guard refuses. It works in the constructor, the first moment the host asks.
/// </summary>
internal sealed class EfMlDeclarationCheck<TContext> : IHostedService where TContext : DbContext
{
    public EfMlDeclarationCheck(IServiceScopeFactory scopes, IReadOnlyList<IEfDatasetPlan> plans)
    {
        using var scope = scopes.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<TContext>().Model;
        var guard = scope.ServiceProvider.GetService<IMlReadGuard>();
        var problems = new List<string>();
        problems.AddRange(EfMlDeclarations.LayerProblems(scope.ServiceProvider));

        foreach (var plan in plans)
        {
            problems.AddRange(EfMlDeclarations.ModelProblems(plan, model, typeof(TContext)));
            problems.AddRange(EfMlDeclarations.Refusals(plan, guard));
        }

        if (problems.Count == 0) return;
        throw new InvalidOperationException("AddIyuMlEntityFramework cannot read the declared datasets: " + string.Join("; ", problems) + ".");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
