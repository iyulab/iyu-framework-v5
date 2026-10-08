using System.Collections.Generic;
using System.Reflection;
using Iyu.Server.OData;

namespace Iyu.MainServer;

/// <summary>
/// Configuration surface for <c>AddIyuMainServer</c>. Consumers populate OData registrations here via
/// <see cref="ODataModel"/>, and any further surface through <see cref="Surface{TSurface}"/>; the composite
/// extension then wires them into the ASP.NET Core pipeline.
/// </summary>
/// <remarks>
/// GraphQL is such a surface, in its own package: reference <c>Iyu.MainServer.GraphQL</c> and
/// <c>options.GraphQL</c> is there — an extension property in this namespace, so registration code reads the same.
/// </remarks>
public sealed class IyuMainServerOptions
{
    private readonly Dictionary<Type, IIyuMainServerSurface> _surfaces = new();

    /// <summary>OData EDM model + entity pair registry.</summary>
    public IyuEdmModelBuilder ODataModel { get; } = new();

    /// <summary>
    /// The host's instance of an optional surface — created the first time it is asked for, the same one after.
    /// A surface nobody asked for is not composed in.
    /// </summary>
    public TSurface Surface<TSurface>() where TSurface : class, IIyuMainServerSurface, new()
    {
        if (!_surfaces.TryGetValue(typeof(TSurface), out var surface))
            _surfaces[typeof(TSurface)] = surface = new TSurface();
        return (TSurface)surface;
    }

    /// <summary>The surfaces asked for, in the order they were first asked for.</summary>
    internal IEnumerable<IIyuMainServerSurface> Surfaces => _surfaces.Values;

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
