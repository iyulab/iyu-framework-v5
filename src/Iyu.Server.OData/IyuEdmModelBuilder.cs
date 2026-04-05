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

    /// <summary>Finalizes the EDM model.</summary>
    public IEdmModel GetEdmModel() => _modelBuilder.GetEdmModel();
}
