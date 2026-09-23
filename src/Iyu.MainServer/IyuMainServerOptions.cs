using System.Collections.Generic;
using System.Reflection;
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

    /// <summary>
    /// Whether OData error responses carry <c>innererror</c> — the exception type and stack trace
    /// behind the error. <see langword="null"/> (the default) includes it only when the host
    /// environment is Development; <see langword="true"/> or <see langword="false"/> overrides that.
    /// </summary>
    /// <remarks>
    /// The top-level <c>message</c> is always written: it says what is wrong with the request (an
    /// unknown property, a malformed literal), which a caller needs to correct it. What this
    /// setting withholds is the part that only describes the server's internals.
    /// </remarks>
    public bool? IncludeODataErrorDetails { get; set; }

    /// <summary>
    /// Extra assemblies whose controllers must be registered as MVC application
    /// parts. <c>AddIyuMainServer</c> already auto-registers the assemblies of the
    /// <c>TContext</c> and of the registration callback's declaring type — which
    /// covers the standard method-group pattern
    /// (<c>configure: ApiRegistration.RegisterGeneratedEntities</c>). Use this only
    /// as an explicit escape hatch when the generated controllers live elsewhere,
    /// or when the callback is a lambda wrapper (whose declaring type resolves to
    /// the caller rather than the controller-hosting assembly). Registration is
    /// deduplicated, so listing an already-discovered assembly is harmless.
    /// </summary>
    /// <remarks>
    /// This exists because MVC's default part discovery walks the entry assembly's
    /// closure. In production the entry assembly is the server, so the generated
    /// controllers are found; under a test host (entry = <c>testhost</c>) or any
    /// non-standard host they are not, and every endpoint silently 404s.
    /// </remarks>
    public IList<Assembly> ControllerAssemblies { get; } = new List<Assembly>();
}
