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

    /// <summary>
    /// Registers a pair. Throws on duplicate set name or conflicting read-type
    /// registration — both indicate a configuration bug that should fail loudly.
    /// </summary>
    public void Register<TRead, TWrite>(string setName)
        where TRead : IyuEntity
        where TWrite : IyuEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);

        var pair = new EntityPair(setName, typeof(TRead), typeof(TWrite));
        if (!_bySetName.TryAdd(setName, pair))
            throw new InvalidOperationException($"Entity set '{setName}' is already registered.");
        if (!_byReadType.TryAdd(typeof(TRead), setName))
        {
            _bySetName.TryRemove(setName, out _);
            throw new InvalidOperationException(
                $"Read type '{typeof(TRead).FullName}' is already registered under set '{_byReadType[typeof(TRead)]}'.");
        }
    }

    /// <summary>Looks up a pair by set name; returns <c>null</c> if unknown.</summary>
    public EntityPair? Find(string setName)
        => _bySetName.TryGetValue(setName, out var pair) ? pair : null;

    /// <summary>Enumerates all registered pairs (snapshot).</summary>
    public IReadOnlyCollection<EntityPair> All => _bySetName.Values.ToList();

    public sealed record EntityPair(string SetName, Type ReadType, Type WriteType);
}
