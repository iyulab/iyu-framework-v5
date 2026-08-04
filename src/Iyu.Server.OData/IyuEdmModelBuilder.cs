using System.Linq.Expressions;
using Iyu.Core.Entities;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace Iyu.Server.OData;

/// <summary>
/// Fluent builder for the Iyu OData EDM model. Wraps
/// <see cref="ODataConventionModelBuilder"/> and a companion
/// <see cref="IyuEntityPairRegistry"/> so that a single call registers both
/// sides of a read/write entity pair under one entity set.
/// </summary>
/// <remarks>
/// The OData model itself exposes only the read (view-backed) type as the
/// entity set's element type. Writes (POST/PATCH) are dispatched to the write
/// (table-backed) type by the generic controller via the registry.
/// </remarks>
public sealed class IyuEdmModelBuilder
{
    private readonly ODataConventionModelBuilder _modelBuilder = new();

    /// <summary>The read/write pair registry populated by calls to <see cref="AddEntityPair{TRead,TWrite}"/>.</summary>
    public IyuEntityPairRegistry Registry { get; } = new();

    /// <summary>
    /// Registers a read/write entity pair under <paramref name="setName"/>.
    /// Only <typeparamref name="TRead"/> is exposed as an OData entity set; the
    /// write type remains internal to the runtime.
    /// </summary>
    public IyuEdmModelBuilder AddEntityPair<TRead, TWrite>(string setName)
        where TRead : IyuEntity
        where TWrite : IyuEntity
    {
        Registry.Register<TRead, TWrite>(setName);
        _modelBuilder.EntitySet<TRead>(setName);
        return this;
    }

    /// <summary>
    /// Removes <paramref name="properties"/> from the exposed model of
    /// <typeparamref name="T"/>, so that the EDM has no such property at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For values that are stored but must never leave the server — password hashes,
    /// client secrets. Because the property is absent from the EDM rather than merely
    /// blanked, <c>$select</c>, <c>$filter</c> and <c>$orderby</c> that name it are
    /// rejected: a caller cannot read the value, and cannot probe it one character at
    /// a time with <c>startswith</c> either. Returning an empty value would leave both
    /// doors open and would be indistinguishable from "the row has no value".
    /// </para>
    /// <para>
    /// Callable after <see cref="AddEntityPair{TRead,TWrite}"/> — the model is not
    /// finalized until <see cref="GetEdmModel"/>. That ordering matters for consumers
    /// whose entity registration is code-generated: they can subtract from a
    /// registration they do not own. Apply it to the write type as well when the
    /// value must not be settable through the generic write path.
    /// </para>
    /// </remarks>
    public IyuEdmModelBuilder Exclude<T>(params Expression<Func<T, object?>>[] properties)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(properties);

        var configuration = _modelBuilder.AddEntityType(typeof(T));
        foreach (var expression in properties)
            configuration.RemoveProperty(ExposedProperty.Resolve(expression));

        return this;
    }

    /// <summary>Finalizes the EDM model.</summary>
    public IEdmModel GetEdmModel() => _modelBuilder.GetEdmModel();
}
