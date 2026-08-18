using System.Reflection;
using System.Runtime.Serialization;

namespace Iyu.Data;

/// <summary>
/// Reflects an enum type's <see cref="EnumMemberAttribute"/> declarations once
/// and caches the CLR-value ↔ wire-name lookup — the one piece of knowledge
/// <see cref="EnumMemberConverter{TEnum}"/> (EF Core) and
/// <see cref="EnumMemberJsonConverterFactory"/> (System.Text.Json) both need,
/// so the two independently-driven serializers cannot drift on what the wire
/// form of an enum is.
/// </summary>
/// <remarks>
/// A value with no <see cref="EnumMemberAttribute"/> falls back to its CLR
/// member name in both directions — matching the behavior a caller would get
/// without any converter at all, so an enum with no annotations at all is
/// unaffected by opting a property/surface into either converter.
/// </remarks>
internal static class EnumWireNames<TEnum>
    where TEnum : struct, Enum
{
    public static readonly IReadOnlyDictionary<TEnum, string> ToWire;
    public static readonly IReadOnlyDictionary<string, TEnum> FromWire;
    public static readonly bool HasAnnotations;

    static EnumWireNames()
    {
        var type = typeof(TEnum);
        var values = Enum.GetValues<TEnum>();
        var toWire = new Dictionary<TEnum, string>(values.Length);
        var fromWire = new Dictionary<string, TEnum>(values.Length, StringComparer.OrdinalIgnoreCase);
        var hasAnnotations = false;

        foreach (var val in values)
        {
            var name = val.ToString();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            var attr = field?.GetCustomAttribute<EnumMemberAttribute>();
            if (attr is not null) hasAnnotations = true;
            var wireValue = attr?.Value ?? name;
            toWire[val] = wireValue;
            fromWire[wireValue] = val;
        }

        ToWire = toWire;
        FromWire = fromWire;
        HasAnnotations = hasAnnotations;
    }
}
