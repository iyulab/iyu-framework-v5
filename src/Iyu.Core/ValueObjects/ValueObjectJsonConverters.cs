using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iyu.Core.ValueObjects;

/// <summary>
/// JsonConverter for <see cref="PhoneNumber"/>: serializes as a plain JSON
/// string ("010-1234-5678") instead of {"Value":"..."}. Null/empty input
/// and invalid phone strings both deserialize to <c>default(PhoneNumber)</c>.
/// </summary>
public sealed class PhoneNumberJsonConverter : JsonConverter<PhoneNumber>
{
    public override PhoneNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s)) return default;
        return PhoneNumber.TryParse(s, out var result) ? result : default;
    }

    public override void Write(Utf8JsonWriter writer, PhoneNumber value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>
/// JsonConverter for <see cref="EmailAddress"/>: serializes as a plain JSON
/// string. Invalid or empty input deserializes to <c>default(EmailAddress)</c>.
/// </summary>
public sealed class EmailAddressJsonConverter : JsonConverter<EmailAddress>
{
    public override EmailAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s)) return default;
        return EmailAddress.TryParse(s, out var result) ? result : default;
    }

    public override void Write(Utf8JsonWriter writer, EmailAddress value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>
/// JsonConverter for <see cref="WebUrl"/>: serializes as a plain JSON string.
/// Invalid or empty input deserializes to <c>default(WebUrl)</c>.
/// </summary>
public sealed class WebUrlJsonConverter : JsonConverter<WebUrl>
{
    public override WebUrl Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s)) return default;
        return WebUrl.TryParse(s, out var result) ? result : default;
    }

    public override void Write(Utf8JsonWriter writer, WebUrl value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
