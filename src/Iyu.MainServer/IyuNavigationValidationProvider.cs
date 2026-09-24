using Iyu.Core.Entities;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Iyu.MainServer;

/// <summary>
/// Excludes the navigation properties of a registered read type from model validation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IyuODataController{TRead, TWrite}"/> binds a POST or PATCH body as the read type. A
/// navigation on that type belongs to the read shape — <c>$expand</c> fills it; a create or an
/// update copies scalars to the write entity and never reads it. Yet a navigation declared the way
/// EF Core recommends for a required relationship (<c>Parent Parent { get; set; } = null!;</c>) is,
/// to ASP.NET Core, an implicitly required input, so without this every well-formed create of such
/// a type is refused with a 400 naming a property the caller cannot meaningfully supply.
/// </para>
/// <para>
/// The exclusion is deliberately narrow. It applies only to types registered as the read half of an
/// entity pair — a consumer's own MVC models are untouched — and only to properties whose type is
/// an <see cref="IyuEntity"/> or a collection of one. Scalar validation, including the same implicit
/// non-nullable rule, still runs on everything else. Setting the property's validation filter,
/// rather than removing its validators, skips the whole entry: an entity sent inside a navigation
/// is not validated either, which matches the write path ignoring it.
/// </para>
/// </remarks>
internal sealed class IyuNavigationValidationProvider(IyuEntityPairRegistry registry) : IValidationMetadataProvider
{
    /// <inheritdoc />
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        if (context.Key.MetadataKind != ModelMetadataKind.Property) return;
        if (context.Key.ContainerType is not { } container) return;
        if (!IsNavigationType(context.Key.ModelType)) return;
        if (registry.FindByReadType(container) is null) return;

        context.ValidationMetadata.PropertyValidationFilter = SkipValidation.Instance;
    }

    private static bool IsNavigationType(Type type)
    {
        if (typeof(IyuEntity).IsAssignableFrom(type)) return true;
        return type.IsGenericType
            && type.GetInterfaces().Append(type).Any(i =>
                i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && typeof(IyuEntity).IsAssignableFrom(i.GetGenericArguments()[0]));
    }

    private sealed class SkipValidation : IPropertyValidationFilter
    {
        public static readonly SkipValidation Instance = new();

        public bool ShouldValidateEntry(ValidationEntry entry, ValidationEntry parentEntry) => false;
    }
}
