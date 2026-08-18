using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iyu.Data;

/// <summary>
/// Serializes an enum using its <see cref="System.Runtime.Serialization.EnumMemberAttribute"/>
/// wire name (via <see cref="EnumWireNames{TEnum}"/>) instead of the plain
/// <c>JsonStringEnumConverter</c>'s CLR member name.
/// </summary>
/// <remarks>
/// <para>
/// <c>Iyu.Server.OData.IyuEdmModelBuilder</c> already makes <c>/$data</c> honor
/// <see cref="System.Runtime.Serialization.EnumMemberAttribute"/> for OData
/// enum serialization. A host that also registers a plain
/// <c>JsonStringEnumConverter</c> for its own MVC controllers (<c>/api</c>)
/// would disagree with <c>/$data</c> on the same enum's wire spelling — this
/// converter is what a host registers instead so both surfaces agree by
/// construction.
/// </para>
/// <para>
/// <see cref="CanConvert"/> only claims enum types that carry at least one
/// <see cref="System.Runtime.Serialization.EnumMemberAttribute"/>. An enum
/// with none falls through to whatever converter is registered after this
/// one (typically the plain <c>JsonStringEnumConverter</c>) — unaffected,
/// exactly as it was before this converter existed.
/// </para>
/// </remarks>
public sealed class EnumMemberJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        if (!enumType.IsEnum) return false;

        var hasAnnotationsType = typeof(EnumWireNames<>).MakeGenericType(enumType);
        var hasAnnotations = hasAnnotationsType.GetField(nameof(EnumWireNames<DayOfWeek>.HasAnnotations))!
            .GetValue(null);
        return hasAnnotations is true;
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        var converterType = typeof(EnumMemberJsonConverter<>).MakeGenericType(enumType);
        var converter = (JsonConverter)Activator.CreateInstance(converterType)!;

        return typeToConvert == enumType
            ? converter
            : (JsonConverter)Activator.CreateInstance(
                typeof(NullableEnumMemberJsonConverter<>).MakeGenericType(enumType), converter)!;
    }
}

internal sealed class EnumMemberJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // The plain JsonStringEnumConverter this factory sits ahead of accepts a raw
        // numeric value by default (AllowIntegerValues: true) -- an annotated enum must
        // keep accepting that too, or opting an enum into [EnumMember] would silently
        // narrow what every existing numeric-sending caller could do.
        if (reader.TokenType == JsonTokenType.Number)
            return (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64());

        var raw = reader.GetString();
        if (raw is not null && EnumWireNames<TEnum>.FromWire.TryGetValue(raw, out var value))
            return value;

        throw new JsonException(
            $"'{raw}' is not a valid wire value for enum '{typeof(TEnum).Name}'. " +
            $"Allowed values: {string.Join(", ", EnumWireNames<TEnum>.FromWire.Keys)}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EnumWireNames<TEnum>.ToWire.GetValueOrDefault(value, value.ToString()));
}

internal sealed class NullableEnumMemberJsonConverter<TEnum>(EnumMemberJsonConverter<TEnum> inner)
    : JsonConverter<TEnum?>
    where TEnum : struct, Enum
{
    public override TEnum? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : inner.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, TEnum? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else inner.Write(writer, value.Value, options);
    }
}
