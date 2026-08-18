using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
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
    /// <b>Name the read type of the pair.</b> That single exclusion closes the read
    /// surface and the write surface together, because the generic controller binds
    /// request bodies to the read type: once the property is absent from the model, a
    /// <c>POST</c> or <c>PATCH</c> naming it is rejected before anything is stored.
    /// The write type is not part of the exposed model, so naming it here excludes
    /// nothing and is refused by <see cref="GetEdmModel"/> — an exclusion whose failure
    /// mode is "you believe the value is hidden and it is not" must not be able to
    /// fail silently.
    /// </para>
    /// <para>
    /// Order does not matter: nothing is applied until <see cref="GetEdmModel"/>, so
    /// this may be called before or after <see cref="AddEntityPair{TRead,TWrite}"/>.
    /// That matters for consumers whose entity registration is code-generated — they
    /// can subtract from a registration they do not own.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An entry of <paramref name="properties"/> is not a direct property access.
    /// Thrown here, where the caller wrote it.
    /// </exception>
    public IyuEdmModelBuilder Exclude<T>(params Expression<Func<T, object?>>[] properties)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length == 0) return this;

        // Resolve now so a malformed expression fails at the call site, but apply at
        // GetEdmModel: the type is only known to be exposed once every pair is registered.
        _exclusions.Add((typeof(T), properties.Select(ExposedProperty.Resolve).ToArray()));
        return this;
    }

    private readonly List<(Type Type, PropertyInfo[] Properties)> _exclusions = new();

    /// <summary>Finalizes the EDM model, applying every recorded exclusion first.</summary>
    /// <exception cref="InvalidOperationException">
    /// An exclusion names a type the model does not expose.
    /// </exception>
    public IEdmModel GetEdmModel()
    {
        foreach (var (type, properties) in _exclusions)
        {
            EnsureExposed(type);
            // The type is a registered read type, so this returns the existing
            // configuration rather than declaring a new one.
            var configuration = _modelBuilder.AddEntityType(type);
            foreach (var property in properties)
                configuration.RemoveProperty(property);
        }

        ApplyEnumMemberNames();
        return _modelBuilder.GetEdmModel();
    }

    /// <summary>
    /// Renames every EDM enum member to the wire name its CLR value declares via
    /// <see cref="EnumMemberAttribute"/>, instead of leaving the CLR member name.
    /// </summary>
    /// <remarks>
    /// <see cref="ODataConventionModelBuilder"/> discovers enum types itself while
    /// building the model inside <see cref="ODataConventionModelBuilder.GetEdmModel"/> —
    /// too late for a caller to reconfigure them, since <see cref="ODataModelBuilder.EnumTypes"/>
    /// is still empty at that point (verified: registering an entity set with an enum
    /// property leaves <c>EnumTypes.Count == 0</c> until <c>GetEdmModel()</c> runs). Each
    /// discovered EDM member is also named after the CLR member — <see cref="EnumMemberAttribute"/>
    /// is never consulted. A generated model's enums declare their wire form there (the
    /// same attribute System.Text.Json and the rest of the wire already honor), so
    /// without this fix-up the EDM (and <c>$metadata</c>) advertises one spelling while
    /// every other layer speaks another: a request built from <c>$metadata</c>, using the
    /// declared wire form, fails deserialization with no indication why the value was
    /// wrong.
    /// <para>
    /// So this pre-registers every enum type reachable from a registered read type's own
    /// properties via <see cref="ODataConventionModelBuilder.AddEnumType"/> — which
    /// returns the same configuration convention discovery would have created, not a
    /// duplicate — so its <see cref="EnumMemberConfiguration.Name"/> can be corrected
    /// before the model is actually built. Nested complex-type properties are not walked;
    /// generated entities are flat, so this covers what the pipeline actually produces.
    /// </para>
    /// </remarks>
    private void ApplyEnumMemberNames()
    {
        var enumTypes = Registry.All
            .SelectMany(pair => pair.ReadType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .Where(t => t.IsEnum)
            .Distinct();

        foreach (var enumType in enumTypes)
        {
            var configuration = _modelBuilder.AddEnumType(enumType);
            foreach (var member in configuration.Members)
            {
                var field = enumType.GetField(member.MemberInfo.ToString()!);
                var wireName = field?.GetCustomAttribute<EnumMemberAttribute>()?.Value;
                if (!string.IsNullOrEmpty(wireName))
                    member.Name = wireName;
            }
        }
    }

    /// <summary>
    /// Refuses an exclusion that names a type the model does not expose.
    /// </summary>
    /// <remarks>
    /// Without this the call is worse than a no-op. Silently ignoring it leaves the
    /// caller believing a stored value is hidden when every read of it still succeeds;
    /// declaring the named type instead would <i>add</i> it to the model, so an attempt
    /// to hide one property would publish the rest of that type's shape. The message
    /// names the type to pass instead, because passing the wrong one is the whole
    /// failure mode.
    /// </remarks>
    private void EnsureExposed(Type type)
    {
        var pairs = Registry.All;
        if (pairs.Any(p => p.ReadType == type)) return;

        var asWriteType = pairs.FirstOrDefault(p => p.WriteType == type);
        var exposed = pairs.Select(p => p.ReadType.Name).Order(StringComparer.Ordinal).ToList();
        var known = exposed.Count == 0
            ? "no entity pairs are registered"
            : $"exposed types are: {string.Join(", ", exposed)}";

        throw new InvalidOperationException(asWriteType is not null
            ? $"Cannot exclude a property of '{type.Name}': it is the write type of entity set "
              + $"'{asWriteType.SetName}' and is not part of the exposed model. Exclude "
              + $"'{asWriteType.ReadType.Name}' instead — request bodies bind to the read type, so "
              + "excluding it closes the read surface and the write surface together."
            : $"Cannot exclude a property of '{type.Name}': the model does not expose it ({known}).");
    }
}
