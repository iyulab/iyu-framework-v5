using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Iyu.Core.Entities;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OData.Edm.Vocabularies.V1;
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
    /// <param name="setName">The OData entity set name.</param>
    /// <param name="readOnlyVerbs">
    /// Write verbs the generic controller must refuse for this set — e.g.
    /// <c>AddEntityPair&lt;OrderSummaryExt, OrderSummary&gt;("OrderSummaries", ODataVerb.Post, ODataVerb.Patch, ODataVerb.Delete)</c>
    /// for a set backed entirely by a read-only view. Advertised on
    /// <c>$metadata</c> via the standard OData Capabilities vocabulary
    /// (<c>Org.OData.Capabilities.V1.InsertRestrictions</c> / <c>UpdateRestrictions</c>
    /// / <c>DeleteRestrictions</c>) and enforced by
    /// <c>IyuODataController&lt;TRead,TWrite&gt;</c> at the same time, so a
    /// client that reads the metadata and one that skips it are rejected
    /// identically. Omit for the pre-existing behavior (every verb allowed).
    /// </param>
    public IyuEdmModelBuilder AddEntityPair<TRead, TWrite>(string setName, params ODataVerb[] readOnlyVerbs)
        where TRead : IyuEntity
        where TWrite : IyuEntity
    {
        Registry.Register<TRead, TWrite>(setName, readOnlyVerbs.Length == 0 ? null : new HashSet<ODataVerb>(readOnlyVerbs));
        _modelBuilder.EntitySet<TRead>(setName);
        return this;
    }

    /// <summary>
    /// Restricts an already-registered set's write verbs from a location that does not own the
    /// original <see cref="AddEntityPair{TRead,TWrite}"/> call site — e.g. a code-generated
    /// registration file the consumer does not hand-edit.
    /// </summary>
    /// <remarks>
    /// Order relative to other builder calls does not matter, the same way it does not for
    /// <see cref="Exclude{T}"/>: this only requires that <paramref name="setName"/> was already
    /// registered by the time this call runs, not that nothing else has run yet. It reaches both
    /// surfaces that read <see cref="Registry"/>'s live state — the <c>$metadata</c> Capabilities
    /// annotations built at <see cref="GetEdmModel"/> time, and
    /// <c>IyuODataController&lt;TRead,TWrite&gt;</c>'s per-request dispatch — with a single update.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="setName"/> is not registered.</exception>
    public IyuEdmModelBuilder Restrict(string setName, params ODataVerb[] readOnlyVerbs)
    {
        Registry.Restrict(setName, new HashSet<ODataVerb>(readOnlyVerbs));
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

    /// <summary>
    /// Marks <paramref name="properties"/> as not writable through the generic
    /// OData POST/PATCH surface, while leaving them exposed for reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a value that genuinely belongs in the model — a caller should see it,
    /// filter on it, select it — but must only ever change through a dedicated
    /// domain endpoint (a state-transition action, say), never through a client
    /// handing the generic write path a plain new value. <see cref="Exclude{T}"/>
    /// is the wrong tool here: it removes the property from the EDM entirely,
    /// closing the read side too.
    /// </para>
    /// <para>
    /// <b>Name the read type of the pair</b>, for the same reason
    /// <see cref="Exclude{T}"/> does — request bodies bind to <c>TRead</c>, so a
    /// name from the write type would exclude nothing a client-facing request
    /// could ever have carried.
    /// </para>
    /// <para>
    /// Enforcement happens twice, deliberately: <c>$metadata</c> advertises the
    /// property as <c>Org.OData.Core.V1.Computed</c> (the standard term for
    /// "server-supplied, do not send on insert/update") for a client that reads
    /// the metadata, and <c>IyuODataController&lt;TRead,TWrite&gt;</c> silently
    /// drops the property from what it copies onto the write entity for a client
    /// that does not — the same two-layer pattern <c>AddEntityPair</c>'s
    /// <c>readOnlyVerbs</c> already uses for whole-set restrictions. Silently
    /// dropping rather than rejecting the request matches <c>Computed</c>'s own
    /// definition and the precedent already set for
    /// <c>Id</c>/<c>CreatedAt</c>/<c>UpdatedAt</c> in
    /// <c>IyuODataController{TRead,TWrite}.CopySelectedProperties</c>.
    /// </para>
    /// <para>
    /// Order does not matter, for the same reason as <see cref="Exclude{T}"/>:
    /// nothing is applied until <see cref="GetEdmModel"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An entry of <paramref name="properties"/> is not a direct property access.
    /// </exception>
    public IyuEdmModelBuilder ExcludeFromWrite<T>(params Expression<Func<T, object?>>[] properties)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length == 0) return this;

        _writeExclusions.Add((typeof(T), properties.Select(ExposedProperty.Resolve).ToArray()));
        return this;
    }

    private readonly List<(Type Type, PropertyInfo[] Properties)> _writeExclusions = new();

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

        foreach (var (type, properties) in _writeExclusions)
        {
            EnsureExposed(type);
            var pair = Registry.All.Single(p => p.ReadType == type);
            var names = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Registry.RestrictProperties(pair.SetName, names);
        }

        ApplyEnumMemberNames();
        var edmModel = _modelBuilder.GetEdmModel();

        // ODataConventionModelBuilder always hands back a mutable EdmModel — verified
        // against the pinned Microsoft.OData.ModelBuilder version; a cast failure here
        // would mean that contract changed and the capability annotations below need
        // a different attachment point, not a silent skip.
        if (edmModel is EdmModel mutableModel)
        {
            ApplyCapabilityRestrictions(mutableModel);
            ApplyPropertyDescriptions(mutableModel);
            ApplyWriteExclusionAnnotations(mutableModel);
        }

        return edmModel;
    }

    /// <summary>
    /// Carries a generated entity's <c>[Display(Description = "...")]</c> onto the EDM
    /// property as the standard <c>Org.OData.Core.V1.Description</c> term.
    /// </summary>
    /// <remarks>
    /// The same free text a generated form already shows via <c>[Display]</c>
    /// (<c>EntityPairRenderer.cs</c>, mdd-booster) — this is the OData-client-facing
    /// counterpart, using the ready-made <see cref="CoreVocabularyModel.DescriptionTerm"/>
    /// rather than a hand-declared term (unlike <see cref="ApplyCapabilityRestrictions"/>,
    /// whose Capabilities restriction terms have no built-in type in the pinned
    /// <c>Microsoft.OData.Edm</c> version).
    /// </remarks>
    private void ApplyPropertyDescriptions(EdmModel model)
    {
        foreach (var pair in Registry.All)
        {
            var entityType = model.SchemaElements.OfType<IEdmEntityType>()
                .FirstOrDefault(t => t.Name == pair.ReadType.Name);
            if (entityType is null) continue;

            foreach (var clrProperty in pair.ReadType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var description = clrProperty.GetCustomAttribute<DisplayAttribute>()?.Description;
                if (string.IsNullOrEmpty(description)) continue;

                var edmProperty = entityType.FindProperty(clrProperty.Name);
                if (edmProperty is null) continue;

                var annotation = new EdmVocabularyAnnotation(
                    edmProperty, CoreVocabularyModel.DescriptionTerm, new EdmStringConstant(description));
                annotation.SetSerializationLocation(model, EdmVocabularyAnnotationSerializationLocation.Inline);
                model.AddVocabularyAnnotation(annotation);
            }
        }
    }

    /// <summary>
    /// Advertises every registered pair's <see cref="IyuEntityPairRegistry.EntityPair.ReadOnlyVerbs"/>
    /// on <c>$metadata</c> via the standard OData Capabilities vocabulary
    /// (<c>Org.OData.Capabilities.V1</c>) — <c>InsertRestrictions</c> /
    /// <c>UpdateRestrictions</c> / <c>DeleteRestrictions</c>, each an
    /// <c>Insertable</c>/<c>Updatable</c>/<c>Deletable</c> boolean record applied
    /// to the entity set.
    /// </summary>
    /// <remarks>
    /// <see cref="Microsoft.OData.Edm.Vocabularies.V1.CapabilitiesVocabularyModel"/>
    /// (the pinned <c>Microsoft.OData.Edm</c> version's only built-in Capabilities
    /// support) declares nothing but <c>ChangeTracking</c> — the restriction terms
    /// used here are not shipped as ready-made types. Declaring them by hand, under
    /// the real standard namespace and with the real standard property names
    /// (rather than an <c>Iyu.*</c> vendor term), costs a few extra lines here and
    /// buys every OData-aware client the annotation for free: no client-side
    /// special-casing, and no migration if a future SDK version ships the same
    /// terms as first-class types — the namespace and shape already match.
    /// </remarks>
    private void ApplyCapabilityRestrictions(EdmModel model)
    {
        var restricted = Registry.All.Where(p => p.ReadOnlyVerbs.Count > 0).ToList();
        if (restricted.Count == 0) return;

        const string ns = "Org.OData.Capabilities.V1";

        var insertType = new EdmComplexType(ns, "InsertRestrictionsType");
        insertType.AddStructuralProperty("Insertable", EdmPrimitiveTypeKind.Boolean, isNullable: false);
        model.AddElement(insertType);
        var insertTerm = new EdmTerm(ns, "InsertRestrictions", new EdmComplexTypeReference(insertType, false), "EntitySet");
        model.AddElement(insertTerm);

        var updateType = new EdmComplexType(ns, "UpdateRestrictionsType");
        updateType.AddStructuralProperty("Updatable", EdmPrimitiveTypeKind.Boolean, isNullable: false);
        model.AddElement(updateType);
        var updateTerm = new EdmTerm(ns, "UpdateRestrictions", new EdmComplexTypeReference(updateType, false), "EntitySet");
        model.AddElement(updateTerm);

        var deleteType = new EdmComplexType(ns, "DeleteRestrictionsType");
        deleteType.AddStructuralProperty("Deletable", EdmPrimitiveTypeKind.Boolean, isNullable: false);
        model.AddElement(deleteType);
        var deleteTerm = new EdmTerm(ns, "DeleteRestrictions", new EdmComplexTypeReference(deleteType, false), "EntitySet");
        model.AddElement(deleteTerm);

        foreach (var pair in restricted)
        {
            var entitySet = model.EntityContainer.FindEntitySet(pair.SetName)
                ?? throw new InvalidOperationException(
                    $"Entity set '{pair.SetName}' was registered but is missing from the built EDM model.");

            if (pair.ReadOnlyVerbs.Contains(ODataVerb.Post))
                Annotate(model, entitySet, insertTerm, "Insertable");
            if (pair.ReadOnlyVerbs.Contains(ODataVerb.Patch))
                Annotate(model, entitySet, updateTerm, "Updatable");
            if (pair.ReadOnlyVerbs.Contains(ODataVerb.Delete))
                Annotate(model, entitySet, deleteTerm, "Deletable");
        }
    }

    /// <summary>
    /// Advertises every <see cref="ExcludeFromWrite{T}"/>-marked property as
    /// <c>Org.OData.Core.V1.Computed</c> on <c>$metadata</c>, using the built-in
    /// <see cref="CoreVocabularyModel.ComputedTerm"/> rather than a hand-declared
    /// one — unlike <see cref="ApplyCapabilityRestrictions"/>'s Capabilities
    /// terms, the pinned <c>Microsoft.OData.Edm</c> version already ships this one.
    /// </summary>
    private void ApplyWriteExclusionAnnotations(EdmModel model)
    {
        foreach (var pair in Registry.All.Where(p => p.WriteExcludedProperties.Count > 0))
        {
            var entityType = model.SchemaElements.OfType<IEdmEntityType>()
                .FirstOrDefault(t => t.Name == pair.ReadType.Name);
            if (entityType is null) continue;

            foreach (var propertyName in pair.WriteExcludedProperties)
            {
                var edmProperty = entityType.FindProperty(propertyName);
                if (edmProperty is null) continue;

                var annotation = new EdmVocabularyAnnotation(
                    edmProperty, CoreVocabularyModel.ComputedTerm, new EdmBooleanConstant(true));
                annotation.SetSerializationLocation(model, EdmVocabularyAnnotationSerializationLocation.Inline);
                model.AddVocabularyAnnotation(annotation);
            }
        }
    }

    /// <summary>Attaches a <c>{propertyName}: false</c> record annotation for <paramref name="term"/>.</summary>
    private static void Annotate(EdmModel model, IEdmEntitySet entitySet, EdmTerm term, string propertyName)
    {
        var annotation = new EdmVocabularyAnnotation(
            entitySet, term,
            new EdmRecordExpression(new EdmPropertyConstructor(propertyName, new EdmBooleanConstant(false))));
        annotation.SetSerializationLocation(model, EdmVocabularyAnnotationSerializationLocation.Inline);
        model.AddVocabularyAnnotation(annotation);
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
