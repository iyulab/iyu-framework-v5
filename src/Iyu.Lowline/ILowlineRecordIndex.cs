using Lowline;

namespace Iyu.Lowline;

/// <summary>
/// What the recovery pass needs to know about the store that <see cref="ILowlineStore"/> does not say: which scopes hold
/// records, and the ids a scope holds without reading the records themselves.
/// </summary>
public interface ILowlineRecordIndex
{
    /// <summary>Every scope (form within memory space) the store holds at least one record for.</summary>
    Task<IReadOnlyList<LowlineScope>> ListScopesAsync(CancellationToken cancellationToken = default);

    /// <summary>The ids of the records <paramref name="scope"/> holds.</summary>
    Task<IReadOnlyList<string>> ListRecordIdsAsync(LowlineScope scope, CancellationToken cancellationToken = default);
}
