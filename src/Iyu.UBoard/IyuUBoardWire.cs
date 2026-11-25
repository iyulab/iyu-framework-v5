using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iyu.UBoard;

/// <summary>
/// The JSON the module's routes write. The screen that reads it ships in the same package, so the names are the module's to fix and do
/// not follow the host's own JSON options: a host that writes its API in PascalCase still serves the screen the camelCase it reads.
/// </summary>
internal static class IyuUBoardWire
{
    /// <summary>camelCase names, the web defaults, nulls written.</summary>
    internal static readonly JsonSerializerOptions Options = Frozen(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>As <see cref="Options"/>, with null members left out.</summary>
    internal static readonly JsonSerializerOptions OmittingNulls = Frozen(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    private static JsonSerializerOptions Frozen(JsonSerializerOptions options)
    {
        options.TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver;
        options.MakeReadOnly();
        return options;
    }
}
