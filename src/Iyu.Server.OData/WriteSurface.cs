using System.Reflection;
using Iyu.Core.Entities;

namespace Iyu.Server.OData;

/// <summary>
/// Which properties of a read type the generic write path can store, and why it cannot store the rest.
/// </summary>
/// <remarks>
/// <b>This is the single place that decides writability.</b> The update path answers the caller from it and
/// <c>$metadata</c> advertises from it, so what a client reads in the metadata and what a write is refused for
/// cannot disagree. A second predicate kept beside it would drift the first time a rule is added.
/// </remarks>
internal sealed class WriteSurface
{
    /// <summary>Why a property cannot be stored by a generic write.</summary>
    internal enum Refusal
    {
        /// <summary>It can be stored.</summary>
        None,
        /// <summary>The key and the timestamps the server sets.</summary>
        ServerManaged,
        /// <summary>The row's version, set by the database.</summary>
        RowVersion,
        /// <summary>The model excludes it from writes.</summary>
        Excluded,
        /// <summary>The read type has it and the write type does not: a view produces it, no table stores it.</summary>
        Derived,
        /// <summary>The write type has it under another type the read value cannot be assigned to.</summary>
        TypeMismatch,
    }

    private readonly Dictionary<string, PropertyInfo> _readProperties;
    private readonly Dictionary<string, PropertyInfo> _writableProperties;

    internal WriteSurface(Type readType, Type writeType)
    {
        _readProperties = readType.GetProperties().ToDictionary(p => p.Name, StringComparer.Ordinal);
        _writableProperties = writeType.GetProperties()
            .Where(p => p.CanWrite && p.GetSetMethod(nonPublic: false) is not null)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    /// <param name="name">A property name as the read type spells it.</param>
    /// <param name="excludedFromWrite">The set's <see cref="IyuEdmModelBuilder.ExcludeFromWrite{T}"/> marks, if any.</param>
    /// <param name="rowVersions">The pair's concurrency tokens.</param>
    internal Refusal Classify(string name, IReadOnlySet<string>? excludedFromWrite, IReadOnlySet<string> rowVersions)
    {
        if (name is nameof(IyuEntity.Id) or nameof(IyuEntity.CreatedAt) or nameof(IyuEntity.UpdatedAt))
            return Refusal.ServerManaged;
        if (rowVersions.Contains(name)) return Refusal.RowVersion;
        if (excludedFromWrite is not null && excludedFromWrite.Contains(name)) return Refusal.Excluded;
        if (!_readProperties.TryGetValue(name, out var read) || !_writableProperties.TryGetValue(name, out var write))
            return Refusal.Derived;
        return write.PropertyType.IsAssignableFrom(read.PropertyType) ? Refusal.None : Refusal.TypeMismatch;
    }
}
