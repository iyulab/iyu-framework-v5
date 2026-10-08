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

    private readonly Dictionary<Type, EntityPolicy> _entityPolicies = new();

    /// <summary>
    /// Declares, once, the authorization policies for the entity whose read type is <typeparamref name="TRead"/> —
    /// and every surface that serves it enforces them: the OData set (<paramref name="read"/> for <c>GET</c> and
    /// <c>$expand</c>, <paramref name="write"/> for <c>POST</c>/<c>PATCH</c>, <paramref name="delete"/> for
    /// <c>DELETE</c>, which falls back to <paramref name="write"/>), and the GraphQL query field (<paramref name="read"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A policy belongs to the data, not to the transport it travels over. Declared per surface, a second
    /// surface is a second place to forget it — and the one forgotten is open under the fallback policy. Declared
    /// here, a surface added later enforces what is already declared.
    /// </para>
    /// <para>
    /// The read type is the key because it is what every surface registers. A type no surface serves is refused
    /// when the host is built, as is a second declaration for one type — a typo or a conflict, not a choice.
    /// <c>null</c> for a policy leaves that access under the host's fallback policy.
    /// </para>
    /// </remarks>
    public IyuMainServerOptions Authorize<TRead>(string? read = null, string? write = null, string? delete = null)
        where TRead : class
        => Authorize(typeof(TRead), read, write, delete);

    /// <summary>
    /// <see cref="Authorize{TRead}"/> for a read type known only at run time — a loop over a generated map of
    /// read types to policies, for one.
    /// </summary>
    public IyuMainServerOptions Authorize(Type readType, string? read = null, string? write = null, string? delete = null)
    {
        ArgumentNullException.ThrowIfNull(readType);
        if (read is not null) ArgumentException.ThrowIfNullOrWhiteSpace(read);
        if (write is not null) ArgumentException.ThrowIfNullOrWhiteSpace(write);
        if (delete is not null) ArgumentException.ThrowIfNullOrWhiteSpace(delete);
        if (!_entityPolicies.TryAdd(readType, new EntityPolicy(read, write, delete)))
            throw new InvalidOperationException(
                $"Authorization for '{readType.FullName}' is already declared. Declare an entity's policies once.");
        return this;
    }

    /// <summary>The policies declared with <see cref="Authorize{TRead}"/>, by read type.</summary>
    public IReadOnlyDictionary<Type, EntityPolicy> EntityPolicies => _entityPolicies;

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
