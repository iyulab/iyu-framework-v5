using System.Linq.Expressions;
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
/// recorded for that future use. When that generator lands, it must consult
/// <c>IyuEntityPairRegistry.EntityPair.ReadOnlyVerbs</c> (Iyu.Server.OData) so a
/// set the OData surface refuses POST/PATCH/DELETE for does not grow a GraphQL
/// mutation that bypasses the same restriction.
/// </para>
/// </remarks>
public sealed class IyuGraphQLSchemaBuilder
{
    private readonly List<Action<IObjectTypeDescriptor>> _fieldBuilders = new();
    private readonly HashSet<string> _queryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mutationPrefixes = new(StringComparer.Ordinal);
    private readonly List<(Type Type, Action<IRequestExecutorBuilder> Apply)> _typeCustomizations = new();
    private readonly Dictionary<Type, string> _exposedTypes = new();

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
        _exposedTypes[typeof(TRead)] = queryName;

        _fieldBuilders.Add(descriptor =>
        {
            descriptor.Field(queryName)
                .Type<ListType<ObjectType<TRead>>>()
                .Resolve(ResolveQueryable<TRead>);
        });

        return this;
    }

    /// <summary>
    /// Removes <paramref name="properties"/> from the schema of
    /// <typeparamref name="T"/>, so that no query can select them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GraphQL counterpart of the OData model builder's <c>Exclude</c>: a type
    /// exposed through both surfaces has to be subtracted from both, or the value
    /// simply leaves by the other door. <b>Name the read type of the pair</b> — that is
    /// the type the schema exposes. Naming any other type excludes nothing and is
    /// refused by <see cref="ApplyTo"/>: a type extension for a type no field returns is
    /// discarded during schema construction, which would leave the caller believing a
    /// stored value is hidden while every query for it still succeeds.
    /// </para>
    /// <para>
    /// Order does not matter — nothing is applied until <see cref="ApplyTo"/>, so this
    /// may be called before or after <see cref="AddEntityPair{TRead,TWrite}"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An entry of <paramref name="properties"/> is not a direct property access.
    /// Thrown here, where the caller wrote it.
    /// </exception>
    public IyuGraphQLSchemaBuilder Exclude<T>(params Expression<Func<T, object?>>[] properties)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(properties);

        // Validate now, not at ApplyTo: a malformed expression must fail where the
        // caller wrote it, not later inside schema construction.
        var selectors = properties.ToArray();
        foreach (var selector in selectors) ExposedProperty.Resolve(selector);
        if (selectors.Length == 0) return this;

        // A type *extension* merges into the type HotChocolate already inferred for T.
        // Registering another ObjectType<T> would be a second, competing registration and
        // the inferred one would keep the field — the exclusion would silently do nothing.
        // Ignore by selector, not by name: the schema field is camelCased, so passing the
        // CLR name would add and hide a *different* field and leave the real one exposed.
        _typeCustomizations.Add((typeof(T), builder => builder.AddTypeExtension(new ObjectTypeExtension<T>(descriptor =>
        {
            foreach (var selector in selectors) descriptor.Ignore(selector);
        }))));

        return this;
    }

    /// <summary>
    /// Refuses an exclusion that names a type the schema does not expose.
    /// </summary>
    /// <remarks>
    /// HotChocolate drops a type extension whose target type no field returns, so
    /// without this the call is a silent no-op — the worst possible outcome for a
    /// feature whose job is keeping a stored value off the wire. The message names the
    /// type to pass instead, because passing the wrong one is the whole failure mode.
    /// </remarks>
    private void EnsureExposed(Type type)
    {
        if (_exposedTypes.ContainsKey(type)) return;

        var exposed = _exposedTypes.Keys.Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        var known = exposed.Count == 0
            ? "no entity pairs are registered"
            : $"exposed types are: {string.Join(", ", exposed)}";

        throw new InvalidOperationException(
            $"Cannot exclude a property of '{type.Name}': the schema does not expose it ({known}). "
            + "Name the read type of the pair — it is the type queries return.");
    }

    /// <summary>
    /// Registers the accumulated pairs as the root <c>Query</c> type on the
    /// given executor builder. Call once during service configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An exclusion names a type the schema does not expose.
    /// </exception>
    public void ApplyTo(IRequestExecutorBuilder executorBuilder)
    {
        ArgumentNullException.ThrowIfNull(executorBuilder);
        var fieldBuilders = _fieldBuilders.ToArray(); // capture snapshot
        executorBuilder.AddQueryType(descriptor =>
        {
            descriptor.Name("Query");
            foreach (var build in fieldBuilders) build(descriptor);
        });
        foreach (var (type, customize) in _typeCustomizations.ToArray())
        {
            EnsureExposed(type);
            customize(executorBuilder);
        }
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
