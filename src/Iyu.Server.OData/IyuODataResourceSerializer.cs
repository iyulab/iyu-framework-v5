using Microsoft.AspNetCore.OData.Edm;
using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.OData;
using Microsoft.OData.Edm;

namespace Iyu.Server.OData;

/// <summary>
/// The Iyu runtime's resource serializer: ASP.NET Core OData's own, with <c>$apply</c> results
/// writing enum values the same way an entity read does.
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
/// TODO(upstream): remove this serializer once Microsoft.AspNetCore.OData writes dynamic-type
/// enum values through <c>ClrEnumMemberAnnotation</c> (<c>CreateODataPropertiesFromDynamicType</c>,
/// unchanged through 9.5.0). <c>EnumInApplyEndToEndTests</c> stays — it must pass without it.
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
                ? new WireEnumView(wrapper, model)
                : graph,
            expectedType, writer, writeContext!);

    private sealed class WireEnumView(DynamicTypeWrapper inner, IEdmModel model) : DynamicTypeWrapper
    {
        private Dictionary<string, object>? _values;

        public override Dictionary<string, object> Values => _values ??= inner.Values.ToDictionary(
            entry => entry.Key,
            entry => Map(entry.Value, model),
            inner.Values.Comparer);

        private static object Map(object value, IEdmModel model) => value switch
        {
            DynamicTypeWrapper nested => new WireEnumView(nested, model),
            IEnumerable<DynamicTypeWrapper> many => many.Select(w => (DynamicTypeWrapper)new WireEnumView(w, model)).ToList(),
            Enum member => WireValue(member, model) ?? value,
            _ => value,
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
