using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
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
/// mutation that bypasses the same restriction — and it should let the same
/// <c>authorizePolicy</c> a pair registered for reads cover its mutations too, so a write
/// surface does not reopen the read-side authorization gap this parameter closes.
/// </para>
/// </remarks>
public sealed class IyuGraphQLSchemaBuilder
{
    private readonly List<Action<IObjectTypeDescriptor>> _fieldBuilders = new();
    private readonly HashSet<string> _queryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mutationPrefixes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _authorizePolicies = new(StringComparer.Ordinal);
    private readonly List<(Type Type, Action<IRequestExecutorBuilder> Apply)> _typeCustomizations = new();
    private readonly Dictionary<Type, string> _exposedTypes = new();
    private bool _usesAuthorization;
    private bool _applied;

    /// <summary>
    /// Registers a query field named <paramref name="queryName"/> that returns
    /// <c>IQueryable&lt;TRead&gt;</c> resolved from the current
    /// <see cref="IyuDbContext"/>. <paramref name="mutationPrefix"/> is
    /// recorded for future mutation generation.
    /// </summary>
    /// <param name="queryName">The GraphQL query field name.</param>
    /// <param name="mutationPrefix">Recorded for future mutation generation; not used yet.</param>
    /// <param name="authorizePolicy">
    /// An ASP.NET Core authorization policy name (e.g. one registered by
    /// <c>Iyu.MainServer.Identity.AddIyuIdentity</c>'s permission catalog) required to read this
    /// field. <see langword="null"/> (the default) leaves the field covered by whatever
    /// <c>FallbackPolicy</c> is configured — the same posture every field had before this
    /// parameter existed. Passing a policy is how a GraphQL query field reaches the same
    /// per-entity authorization OData's <c>IyuODataController</c> gets for free from ASP.NET Core
    /// MVC's convention pipeline; <see cref="ApplyTo"/> wires the bridge handler that enforces it
    /// automatically the first time any pair uses this parameter.
    /// </param>
    public IyuGraphQLSchemaBuilder AddEntityPair<TRead, TWrite>(
        string queryName, string mutationPrefix, string? authorizePolicy = null)
        where TRead : class
        where TWrite : IyuEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationPrefix);
        if (authorizePolicy is not null) ArgumentException.ThrowIfNullOrWhiteSpace(authorizePolicy);
        if (!_queryNames.Add(queryName))
            throw new InvalidOperationException($"GraphQL query field '{queryName}' is already registered.");
        _mutationPrefixes[queryName] = mutationPrefix;
        _exposedTypes[typeof(TRead)] = queryName;
        _authorizePolicies[queryName] = authorizePolicy;
        if (authorizePolicy is not null) _usesAuthorization = true;

        // Reads _authorizePolicies at build time (deferred to ApplyTo's AddQueryType callback,
        // which HotChocolate does not invoke until schema construction) rather than closing over
        // the authorizePolicy parameter directly, so a later Restrict() call for this queryName
        // is still picked up — the same registration-call-site independence
        // IyuEntityPairRegistry.Restrict gives the OData surface (Iyu.Server.OData).
        _fieldBuilders.Add(descriptor =>
        {
            var field = descriptor.Field(queryName)
                .Type<ListType<ObjectType<TRead>>>()
                .Resolve(ResolveQueryable<TRead>);
            if (_authorizePolicies[queryName] is { } policy) field.Authorize(policy);
        });

        ApplyPropertyDescriptions<TRead>();
        return this;
    }

    /// <summary>
    /// Applies (or replaces) the authorization policy for an already-registered query field, from
    /// a location that does not own the original <see cref="AddEntityPair{TRead,TWrite}"/> call
    /// site — e.g. a code-generated registration file the consumer does not hand-edit. The
    /// GraphQL counterpart of <c>IyuEdmModelBuilder.Restrict</c> (Iyu.Server.OData):
    /// codegen keeps emitting the plain <c>AddEntityPair(queryName, mutationPrefix)</c> call, and
    /// the consumer layers authorization on afterward from its own composition root.
    /// </summary>
    /// <param name="queryName">A field name already registered via <see cref="AddEntityPair{TRead,TWrite}"/>.</param>
    /// <param name="authorizePolicy">
    /// The ASP.NET Core authorization policy name to require for this field — same contract as
    /// <see cref="AddEntityPair{TRead,TWrite}"/>'s <c>authorizePolicy</c> parameter.
    /// </param>
    /// <remarks>
    /// Must be called before <see cref="ApplyTo"/>. Unlike <c>IyuEdmModelBuilder.Restrict</c> —
    /// which only needs to land before the EDM model is finalized, well after service
    /// configuration — this needs <see cref="IyuGraphQLAuthorizationExtensions.AddIyuGraphQLAuthorization"/>
    /// wired into DI, and <see cref="ApplyTo"/> decides whether to do that synchronously, during
    /// service configuration. Calling this after <see cref="ApplyTo"/> throws rather than silently
    /// registering a policy no handler will ever enforce.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="queryName"/> was never registered via <see cref="AddEntityPair{TRead,TWrite}"/>,
    /// or this is called after <see cref="ApplyTo"/>.
    /// </exception>
    public IyuGraphQLSchemaBuilder Restrict(string queryName, string authorizePolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizePolicy);
        if (_applied)
            throw new InvalidOperationException(
                $"Cannot restrict GraphQL query field '{queryName}': ApplyTo has already run. "
                + "Call Restrict before ApplyTo — it wires the authorization handler into DI synchronously.");
        if (!_queryNames.Contains(queryName))
            throw new InvalidOperationException(
                $"Cannot restrict GraphQL query field '{queryName}': it was never registered via AddEntityPair.");
        _authorizePolicies[queryName] = authorizePolicy;
        _usesAuthorization = true;
        return this;
    }

    /// <summary>
    /// Carries a generated entity's <c>[Display(Description = "...")]</c> onto the GraphQL
    /// field as its standard <c>description</c> — the same free text a generated form already
    /// shows via <c>[Display]</c> (<c>EntityPairRenderer.cs</c>, mdd-booster), now visible in
    /// schema introspection too.
    /// </summary>
    /// <remarks>
    /// Reuses the same type-extension mechanism as <see cref="Exclude{T}"/>: a type extension
    /// merges into the type HotChocolate already inferred for <typeparamref name="TRead"/>
    /// rather than competing with it. Registered unconditionally at <see cref="AddEntityPair"/>
    /// time (unlike <c>Exclude</c>, which is opt-in) because it never removes anything the
    /// caller relies on — a property with no <c>[Display]</c> keeps its convention-inferred
    /// description (none).
    /// </remarks>
    private void ApplyPropertyDescriptions<TRead>() where TRead : class
    {
        var descriptions = typeof(TRead).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Description: p.GetCustomAttribute<DisplayAttribute>()?.Description))
            .Where(x => !string.IsNullOrEmpty(x.Description))
            .ToArray();
        if (descriptions.Length == 0) return;

        _typeCustomizations.Add((typeof(TRead), builder => builder.AddTypeExtension(new ObjectTypeExtension<TRead>(descriptor =>
        {
            foreach (var (property, description) in descriptions)
                descriptor.Field(property).Description(description!);
        }))));
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
        _applied = true;
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
        if (_usesAuthorization)
            executorBuilder.AddIyuGraphQLAuthorization();
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
