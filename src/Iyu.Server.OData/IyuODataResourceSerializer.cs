using Microsoft.AspNetCore.OData.Edm;
using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.OData;
using Microsoft.OData.Edm;

namespace Iyu.Server.OData;

/// <summary>
/// The Iyu runtime's resource serializer: ASP.NET Core OData's own, with <c>$apply</c> results
/// writing enum values the same way an entity read does, and a group with no reference writing
/// that reference as null.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IyuEdmModelBuilder"/> names each EDM enum member after its
/// <c>[EnumMember(Value = ...)]</c> wire value. An entity read writes that name — the enum
/// serializer maps the CLR value through the model's <c>ClrEnumMemberAnnotation</c>. A
/// <c>groupby</c> result is a <see cref="DynamicTypeWrapper"/>, and the stock serializer writes
/// its values with the primitive converter instead, which is <c>ToString()</c> for an enum: the
/// same property came back as <c>print_order</c> from <c>$select</c> and <c>PrintOrder</c> from
/// <c>$apply</c>, and a grouped value sent back as a <c>$filter</c> was rejected.
/// </para>
/// <para>
/// That write path is private, so this serializer hands the stock one a view of the wrapper whose
/// enum values are already their EDM member names — the same map the enum serializer uses, so the
/// two paths cannot disagree. Nested wrappers (<c>groupby</c> over a navigation path) get the same
/// view. A value the map does not resolve (a combination of flags) is passed through unchanged.
/// </para>
/// <para>
/// A <c>groupby</c> over a path through an optional reference — <c>groupby((Owner/Id))</c> — puts
/// the rows without one in a group whose nested wrapper holds nothing but nulls, the key included.
/// The stock serializer writes that wrapper as an instance of the reference's type, and the writer
/// stops at the key the model declares non-nullable — after the response's 200 has gone out, so the
/// caller gets a body that breaks off. A nested wrapper whose values are all null, one of them a
/// property no instance can lack, describes no instance: the view writes the reference as null,
/// which is what those rows have. All-null values the model allows (a group of present references
/// with a null property) are left as they are.
/// </para>
/// <para>
/// TODO(upstream): remove this serializer once Microsoft.AspNetCore.OData writes dynamic-type
/// enum values through <c>ClrEnumMemberAnnotation</c> (<c>CreateODataPropertiesFromDynamicType</c>,
/// unchanged through 9.5.0) and writes a group's absent reference as null
/// (<c>WriteDynamicTypeResourceAsync</c>, same release). <c>EnumInApplyEndToEndTests</c> and
/// <c>ApplyOverOptionalReferenceEndToEndTests</c> stay — they must pass without it.
/// </para>
/// </remarks>
public sealed class IyuODataResourceSerializer(IODataSerializerProvider serializerProvider)
    : ODataResourceSerializer(serializerProvider)
{
    /// <inheritdoc />
    public override Task WriteObjectInlineAsync(object graph, IEdmTypeReference expectedType, ODataWriter writer,
        ODataSerializerContext writeContext)
        => base.WriteObjectInlineAsync(
            graph is DynamicTypeWrapper wrapper && writeContext?.Model is { } model
                ? new WireView(wrapper, model, expectedType?.Definition as IEdmStructuredType)
                : graph,
            expectedType!, writer, writeContext!);

    private sealed class WireView(DynamicTypeWrapper inner, IEdmModel model, IEdmStructuredType? type) : DynamicTypeWrapper
    {
        private Dictionary<string, object>? _values;

        public override Dictionary<string, object> Values => _values ??= inner.Values.ToDictionary(
            entry => entry.Key,
            entry => Map(entry.Key, entry.Value),
            inner.Values.Comparer);

        /// <summary>
        /// Every grouped value is null, and one of them is one the model says cannot be: no instance of this type
        /// has those values, so the rows of this group have no such reference at all.
        /// </summary>
        private bool Absent => type is not null
                               && Values.Count > 0
                               && Values.Values.All(v => v is null)
                               && Values.Keys.Any(k => type.FindProperty(k) is { Type.IsNullable: false });

        private object Map(string name, object value) => value switch
        {
            DynamicTypeWrapper nested => new WireView(nested, model, Target(name)) is var view && view.Absent ? null! : view,
            IEnumerable<DynamicTypeWrapper> many => many.Select(w => (DynamicTypeWrapper)new WireView(w, model, Target(name))).ToList(),
            Enum member => WireValue(member, model) ?? value,
            _ => value,
        };

        private IEdmStructuredType? Target(string name) => type?.FindProperty(name)?.Type switch
        {
            { } t when t.IsCollection() => t.AsCollection().ElementType().Definition as IEdmStructuredType,
            { } t => t.Definition as IEdmStructuredType,
            null => null,
        };

        // An ODataEnumValue, not a string: the writer validates an enum-typed property's value
        // against the enum type, and the primitive converter passes this value through untouched.
        private static ODataEnumValue? WireValue(Enum value, IEdmModel model)
            => model.GetTypeMapper().GetEdmTypeReference(model, value.GetType())?.Definition is IEdmEnumType enumType
               && model.GetClrEnumMemberAnnotation(enumType)?.GetEdmEnumMember(value) is { } member
                ? new ODataEnumValue(member.Name, enumType.FullName())
                : null;
    }
}
