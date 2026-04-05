using Iyu.Server.GraphQL;
using Iyu.Server.OData;

namespace Iyu.MainServer;

/// <summary>
/// Configuration surface for <c>AddIyuMainServer</c>. Consumers populate OData
/// and GraphQL registrations here via the two fluent builders; the composite
/// extension then wires them into the ASP.NET Core pipeline.
/// </summary>
public sealed class IyuMainServerOptions
{
    /// <summary>OData EDM model + entity pair registry.</summary>
    public IyuEdmModelBuilder ODataModel { get; } = new();

    /// <summary>GraphQL schema builder (HotChocolate).</summary>
    public IyuGraphQLSchemaBuilder GraphQL { get; } = new();

    /// <summary>
    /// OData route prefix. Defaults to <c>"$data"</c> per the design spec
    /// (resulting URLs of the form <c>/$data/{EntitySet}</c>).
    /// </summary>
    public string ODataRoutePrefix { get; set; } = "$data";
}
