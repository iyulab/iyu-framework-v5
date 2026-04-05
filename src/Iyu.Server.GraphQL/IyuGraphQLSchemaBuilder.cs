using HotChocolate.Execution.Configuration;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Builds the HotChocolate schema for the Iyu runtime by accumulating
/// read/write entity pair registrations and then applying them as a single
/// root <c>Query</c> type. Each registered pair becomes one query field
/// returning <c>IQueryable&lt;TRead&gt;</c>, resolved against the current
/// <see cref="IyuDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="AddEntityPair{TRead,TWrite}"/> zero-or-more times, then
/// call <see cref="ApplyTo(IRequestExecutorBuilder)"/> exactly once during
/// service configuration. <c>ApplyTo</c> invokes <c>AddQueryType</c>, so the
/// caller must not add a Query type separately.
/// </para>
/// <para>
/// Mutations are not wired by the runtime scaffold — they are more
/// application-specific (input shapes, authorization) and will be emitted by
/// the mdd-booster API generator in a later plan. <c>mutationPrefix</c> is
/// recorded for that future use.
/// </para>
/// </remarks>
public sealed class IyuGraphQLSchemaBuilder
{
    private readonly List<Action<IObjectTypeDescriptor>> _fieldBuilders = new();
    private readonly HashSet<string> _queryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mutationPrefixes = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a query field named <paramref name="queryName"/> that returns
    /// <c>IQueryable&lt;TRead&gt;</c> resolved from the current
    /// <see cref="IyuDbContext"/>. <paramref name="mutationPrefix"/> is
    /// recorded for future mutation generation.
    /// </summary>
    public IyuGraphQLSchemaBuilder AddEntityPair<TRead, TWrite>(string queryName, string mutationPrefix)
        where TRead : class
        where TWrite : IyuEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationPrefix);
        if (!_queryNames.Add(queryName))
            throw new InvalidOperationException($"GraphQL query field '{queryName}' is already registered.");
        _mutationPrefixes[queryName] = mutationPrefix;

        _fieldBuilders.Add(descriptor =>
        {
            descriptor.Field(queryName)
                .Type<ListType<ObjectType<TRead>>>()
                .Resolve(ResolveQueryable<TRead>);
        });

        return this;
    }

    /// <summary>
    /// Registers the accumulated pairs as the root <c>Query</c> type on the
    /// given executor builder. Call once during service configuration.
    /// </summary>
    public void ApplyTo(IRequestExecutorBuilder executorBuilder)
    {
        ArgumentNullException.ThrowIfNull(executorBuilder);
        var fieldBuilders = _fieldBuilders.ToArray(); // capture snapshot
        executorBuilder.AddQueryType(descriptor =>
        {
            descriptor.Name("Query");
            foreach (var build in fieldBuilders) build(descriptor);
        });
    }

    /// <summary>Exposes the mutation prefix recorded for a given query name.</summary>
    public string? GetMutationPrefix(string queryName)
        => _mutationPrefixes.TryGetValue(queryName, out var prefix) ? prefix : null;

    /// <summary>Snapshot of all registered query field names.</summary>
    public IReadOnlyCollection<string> QueryNames => _queryNames.ToList();

    private static IQueryable<T> ResolveQueryable<T>(IResolverContext ctx)
        where T : class
    {
        var db = ctx.Service<IyuDbContext>();
        return db.Set<T>().AsNoTracking();
    }
}
