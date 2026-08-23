using System.Collections.Concurrent;
using Iyu.Core.Entities;

namespace Iyu.Server.OData;

/// <summary>
/// Runtime registry of read/write entity pairs keyed by the OData entity set name.
/// Populated by <see cref="IyuEdmModelBuilder.AddEntityPair{TRead,TWrite}"/> and
/// consulted by <c>IyuODataController&lt;TRead,TWrite&gt;</c> to map between the
/// view-backed read type and the table-backed write type.
/// </summary>
public sealed class IyuEntityPairRegistry
{
    private readonly ConcurrentDictionary<string, EntityPair> _bySetName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, string> _byReadType = new();

    private static readonly IReadOnlySet<ODataVerb> NoRestrictions = new HashSet<ODataVerb>();

    /// <summary>
    /// Registers a pair. Throws on duplicate set name or conflicting read-type
    /// registration — both indicate a configuration bug that should fail loudly.
    /// </summary>
    /// <param name="setName">The OData entity set name.</param>
    /// <param name="readOnlyVerbs">
    /// Write verbs this set refuses. <c>null</c>/empty means every verb the
    /// generic controller exposes (POST/PATCH/DELETE) is allowed — the
    /// pre-existing behavior.
    /// </param>
    public void Register<TRead, TWrite>(string setName, IReadOnlySet<ODataVerb>? readOnlyVerbs = null)
        where TRead : IyuEntity
        where TWrite : IyuEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);

        var pair = new EntityPair(setName, typeof(TRead), typeof(TWrite), readOnlyVerbs ?? NoRestrictions);
        if (!_bySetName.TryAdd(setName, pair))
            throw new InvalidOperationException($"Entity set '{setName}' is already registered.");
        if (!_byReadType.TryAdd(typeof(TRead), setName))
        {
            _bySetName.TryRemove(setName, out _);
            throw new InvalidOperationException(
                $"Read type '{typeof(TRead).FullName}' is already registered under set '{_byReadType[typeof(TRead)]}'.");
        }
    }

    /// <summary>
    /// Updates the read-only verb restriction of an already-registered set, in place.
    /// </summary>
    /// <remarks>
    /// For a consumer whose entity registration is code-generated — a single generated file
    /// calls <see cref="IyuEdmModelBuilder.AddEntityPair{TRead,TWrite}"/> for every set with no
    /// per-call-site control — <c>readOnlyVerbs</c> cannot be threaded through that call. This
    /// lets such a consumer restrict a set after the fact, from a location it does own, without
    /// re-registering it. <see cref="Register{TRead,TWrite}"/> still throws on a genuine
    /// duplicate registration; this does not weaken that guard, since it only updates the verb
    /// set of a set already known to be registered.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="setName"/> is not registered.</exception>
    public void Restrict(string setName, IReadOnlySet<ODataVerb> readOnlyVerbs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        ArgumentNullException.ThrowIfNull(readOnlyVerbs);

        _bySetName.AddOrUpdate(
            setName,
            _ => throw new InvalidOperationException($"Entity set '{setName}' is not registered."),
            (_, existing) => existing with { ReadOnlyVerbs = readOnlyVerbs });
    }

    /// <summary>Looks up a pair by set name; returns <c>null</c> if unknown.</summary>
    public EntityPair? Find(string setName)
        => _bySetName.TryGetValue(setName, out var pair) ? pair : null;

    /// <summary>Looks up a pair by its read type; returns <c>null</c> if unknown.</summary>
    public EntityPair? FindByReadType(Type readType)
        => _byReadType.TryGetValue(readType, out var setName) ? Find(setName) : null;

    /// <summary>Enumerates all registered pairs (snapshot).</summary>
    public IReadOnlyCollection<EntityPair> All => _bySetName.Values.ToList();

    public sealed record EntityPair(string SetName, Type ReadType, Type WriteType, IReadOnlySet<ODataVerb> ReadOnlyVerbs);
}
