using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Ml.EntityFrameworkCore;

/// <summary>
/// Reads declared datasets from the application's own <typeparamref name="TContext"/>. A column is a property the host named; a
/// property it did not name is not selected from the database. The caller bounds what comes back: an anonymous caller is
/// refused, and a caller the registered <see cref="IMlReadGuard"/> says may not read the entity gets no rows.
/// </summary>
/// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
public sealed class EfMlDatasetSource<TContext> : IMlDatasetSource where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IReadOnlyList<IEfDatasetPlan> _plans;

    internal EfMlDatasetSource(IServiceScopeFactory scopes, IReadOnlyList<IEfDatasetPlan> plans)
    {
        _scopes = scopes;
        _plans = plans;
        Datasets = plans.Select(p => p.Definition).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<MlDatasetDefinition> Datasets { get; }

    /// <inheritdoc />
    public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadAsync(
        string dataset, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(caller);

        // Refused when asked, not when the first row is awaited: a mistake should fail at the call that made it.
        var plan = _plans.FirstOrDefault(p => string.Equals(p.Definition.Name, dataset, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No dataset is declared under the name '{dataset}'.", nameof(dataset));
        if (caller.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("A dataset is not served to an anonymous caller.");
        return Stream(plan, caller, cancellationToken);
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Stream(
        IEfDatasetPlan plan, ClaimsPrincipal caller,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();

        // The guard first. A column it refuses is refused here as well as when the host starts: this source can be built
        // without the host ever starting, and a column policy that held only at start would then hold nowhere. A caller who may
        // not read the entity gets nothing, and the database is not asked.
        var guard = scope.ServiceProvider.GetService<IMlReadGuard>();
        var refused = EfMlDeclarations.Refusals(plan, guard);
        if (refused.Count > 0)
            throw new InvalidOperationException("The dataset cannot be read: " + string.Join("; ", refused) + ".");
        if (guard is not null && !await guard.MayReadAsync(plan.EntityType, caller, cancellationToken).ConfigureAwait(false))
            yield break;

        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var missing = EfMlDeclarations.ModelProblems(plan, context.Model, typeof(TContext));
        if (missing.Count > 0)
            throw new InvalidOperationException("The dataset cannot be read: " + string.Join("; ", missing) + ".");
        await foreach (var row in plan.ReadAsync(context, cancellationToken).ConfigureAwait(false))
            yield return row;
    }
}
