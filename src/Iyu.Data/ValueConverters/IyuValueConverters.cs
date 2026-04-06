using Iyu.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Iyu.Data.ValueConverters;

/// <summary>
/// EF Core <see cref="ValueConverter"/>s for <see cref="Iyu.Core.ValueObjects"/>
/// types. Each converter maps the value object to its canonical string form for
/// database storage and back. <c>default</c> struct values (where <c>Value</c>
/// is null) are stored as empty string to avoid NRE on materialization.
/// </summary>
/// <remarks>
/// The read-side lambdas delegate to static helpers because
/// <see cref="ValueConverter{TModel,TProvider}"/> accepts an expression tree,
/// and expression trees cannot contain <c>out var</c> declarations. The helpers
/// keep the conversion pure and side-effect free.
/// </remarks>
public static class IyuValueConverters
{
    /// <summary>
    /// Registers all value object converters with the model configuration builder
    /// so that EF Core automatically applies them to any property of these types.
    /// </summary>
    public static void RegisterAll(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<PhoneNumber>().HaveConversion<PhoneNumberConverter>();
        configurationBuilder.Properties<EmailAddress>().HaveConversion<EmailAddressConverter>();
        configurationBuilder.Properties<WebUrl>().HaveConversion<WebUrlConverter>();
    }

    public sealed class PhoneNumberConverter()
        : ValueConverter<PhoneNumber, string>(v => v.Value ?? string.Empty, v => FromPhoneString(v));

    public sealed class EmailAddressConverter()
        : ValueConverter<EmailAddress, string>(v => v.Value ?? string.Empty, v => FromEmailString(v));

    public sealed class WebUrlConverter()
        : ValueConverter<WebUrl, string>(v => v.Value ?? string.Empty, v => FromUrlString(v));

    /// <summary><see cref="PhoneNumber"/> ↔ <see cref="string"/>.</summary>
    public static readonly ValueConverter<PhoneNumber, string> Phone = new(
        v => v.Value ?? string.Empty,
        v => FromPhoneString(v));

    /// <summary><see cref="EmailAddress"/> ↔ <see cref="string"/>.</summary>
    public static readonly ValueConverter<EmailAddress, string> Email = new(
        v => v.Value ?? string.Empty,
        v => FromEmailString(v));

    /// <summary><see cref="WebUrl"/> ↔ <see cref="string"/>.</summary>
    public static readonly ValueConverter<WebUrl, string> Url = new(
        v => v.Value ?? string.Empty,
        v => FromUrlString(v));

    private static PhoneNumber FromPhoneString(string value)
        => PhoneNumber.TryParse(value, out var p) ? p : default;

    private static EmailAddress FromEmailString(string value)
        => EmailAddress.TryParse(value, out var e) ? e : default;

    private static WebUrl FromUrlString(string value)
        => WebUrl.TryParse(value, out var u) ? u : default;
}
