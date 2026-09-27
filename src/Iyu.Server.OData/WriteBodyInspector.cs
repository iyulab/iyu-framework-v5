using System.Globalization;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace Iyu.Server.OData;

/// <summary>A value in a write body that its property's declared type cannot hold.</summary>
/// <param name="Path">The property as a caller would write it (<c>Seq</c>, <c>Address.Zip</c>).</param>
/// <param name="Message">A fixed sentence naming the declared type — never the value that was sent.</param>
internal sealed record UnconvertibleValue(string Path, string Message);

/// <summary>What <see cref="WriteBodyInspector"/> found wrong with a write body.</summary>
internal sealed record WriteBodyFindings(IReadOnlyList<string> Undeclared, IReadOnlyList<UnconvertibleValue> Unconvertible)
{
    public static readonly WriteBodyFindings None = new([], []);
}

/// <summary>
/// Compares a write body that failed to bind against the EDM type of the set it addresses, to name
/// what the reader refused: property names the type does not declare, and values the declared type
/// cannot hold.
/// </summary>
/// <remarks>
/// <para>
/// OData's body reader refuses both by throwing, and the only trace it leaves is the exception's
/// message — text that also carries the EDM type's full name and the literal the caller sent, and
/// whose wording belongs to the package, not to this framework. Recovering a property from that text
/// would tie the answer to a string a package upgrade may reword, and the text does not always name
/// the property at all. Comparing the body against the model needs only what this framework already
/// owns: the EDM type of the set the request addresses, and the body the caller sent.
/// </para>
/// <para>
/// <b>The value checks are deliberately conservative.</b> A value is reported only when its JSON
/// form cannot be the declared type — a string where a number or a boolean is declared, a string
/// that does not parse as the declared Guid or date, an integer outside its range, a name that is
/// not a member of the enumeration. Anything the reader might accept is left alone: reporting a
/// value the reader took would send the caller after the wrong field, which is worse than the
/// generic answer this falls back to when nothing is found.
/// </para>
/// <para>
/// It runs only after the bind has already failed, so a request that binds pays nothing but the
/// buffering <see cref="IyuBufferWriteBodyAttribute"/> sets up.
/// </para>
/// </remarks>
internal static class WriteBodyInspector
{
    /// <summary>
    /// The findings for the request body — none when the body cannot be re-read, is not a JSON
    /// object, or addresses no entity set.
    /// </summary>
    public static async Task<WriteBodyFindings> InspectAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.Body.CanSeek) return WriteBodyFindings.None;
        var type = request.ODataFeature().Path?.OfType<EntitySetSegment>().FirstOrDefault()?.EntitySet.EntityType;
        if (type is null) return WriteBodyFindings.None;

        request.Body.Position = 0;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return WriteBodyFindings.None;
        }

        using (document)
        {
            var undeclared = new List<string>();
            var unconvertible = new List<UnconvertibleValue>();
            Collect(document.RootElement, type, prefix: "", undeclared, unconvertible);
            return new WriteBodyFindings(undeclared, unconvertible);
        }
    }

    private static void Collect(JsonElement value, IEdmStructuredType type, string prefix,
        List<string> undeclared, List<UnconvertibleValue> unconvertible)
    {
        if (value.ValueKind != JsonValueKind.Object || type.IsOpen) return;

        foreach (var member in value.EnumerateObject())
        {
            // Instance and property annotations (@odata.type, Name@odata.type, Customer@odata.bind)
            // are OData's own vocabulary, not properties of the type.
            if (member.Name.Contains('@', StringComparison.Ordinal)) continue;

            var path = prefix + member.Name;
            if (Declared(type, member.Name) is not { } property)
            {
                undeclared.Add(path);
                continue;
            }

            var declared = property.Type;
            if (declared.IsCollection())
            {
                if (member.Value.ValueKind != JsonValueKind.Array) continue;
                var element = declared.AsCollection().ElementType();
                foreach (var item in member.Value.EnumerateArray())
                    CollectValue(item, element, path, undeclared, unconvertible);
            }
            else
            {
                CollectValue(member.Value, declared, path, undeclared, unconvertible);
            }
        }
    }

    private static void CollectValue(JsonElement value, IEdmTypeReference declared, string path,
        List<string> undeclared, List<UnconvertibleValue> unconvertible)
    {
        if (declared.Definition is IEdmStructuredType nested)
        {
            Collect(value, nested, path + ".", undeclared, unconvertible);
            return;
        }
        if (Unconvertible(value, declared) is { } message)
            unconvertible.Add(new UnconvertibleValue(path, message));
    }

    /// <returns>The sentence to report, or <c>null</c> when the value may be what is declared.</returns>
    private static string? Unconvertible(JsonElement value, IEdmTypeReference declared)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;   // nullability is validation's to answer

        if (declared.Definition is IEdmEnumType enumType)
        {
            if (value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString()!;
            // Flags enums arrive comma-separated; a numeric string is the member's underlying value.
            var names = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var fits = names.Length > 0 && names.All(n =>
                long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                || enumType.Members.Any(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase)));
            return fits ? null : $"The value is not a member of {enumType.Name}.";
        }

        if (declared.Definition is not IEdmPrimitiveType primitive) return null;
        var fitsPrimitive = primitive.PrimitiveKind switch
        {
            EdmPrimitiveTypeKind.String => value.ValueKind == JsonValueKind.String,
            EdmPrimitiveTypeKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            EdmPrimitiveTypeKind.Guid => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
            EdmPrimitiveTypeKind.Byte => Integer(value, byte.MinValue, byte.MaxValue),
            EdmPrimitiveTypeKind.SByte => Integer(value, sbyte.MinValue, sbyte.MaxValue),
            EdmPrimitiveTypeKind.Int16 => Integer(value, short.MinValue, short.MaxValue),
            EdmPrimitiveTypeKind.Int32 => Integer(value, int.MinValue, int.MaxValue),
            EdmPrimitiveTypeKind.Int64 => Integer(value, long.MinValue, long.MaxValue),
            EdmPrimitiveTypeKind.Decimal or EdmPrimitiveTypeKind.Double or EdmPrimitiveTypeKind.Single => Number(value),
            EdmPrimitiveTypeKind.DateTimeOffset => value.ValueKind == JsonValueKind.String && IsDateTimeOffset(value.GetString()!),
            EdmPrimitiveTypeKind.Date => value.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            EdmPrimitiveTypeKind.TimeOfDay => value.ValueKind == JsonValueKind.String
                && TimeOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out _),
            EdmPrimitiveTypeKind.Duration => value.ValueKind == JsonValueKind.String && IsDuration(value.GetString()!),
            _ => true,   // spatial, binary, stream — not judged here
        };
        return fitsPrimitive ? null : $"The value could not be converted to {primitive.Name}.";
    }

    /// <summary>
    /// A JSON number that is a whole number within range. A numeric string is let through: under the
    /// IEEE754-compatible format large integers travel as strings, and whether the reader accepts one
    /// is its call, not this check's.
    /// </summary>
    private static bool Integer(JsonElement value, long min, long max) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt64(out var n) && n >= min && n <= max,
        JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        _ => false,
    };

    private static bool Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => true,
        // Decimals may travel as strings (IEEE754-compatible), doubles as NaN / INF / -INF.
        JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || value.GetString() is "NaN" or "INF" or "-INF",
        _ => false,
    };

    /// <summary>ISO 8601 with a time and an explicit offset — the offset is what the reader requires.</summary>
    private static bool IsDateTimeOffset(string text)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
        var t = text.IndexOf('T');
        if (t < 0) return false;
        var time = text[(t + 1)..];
        return time.EndsWith('Z') || time.EndsWith('z') || time.Contains('+') || time.Contains('-');
    }

    private static bool IsDuration(string text)
    {
        try { _ = XmlConvert.ToTimeSpan(text); return true; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    /// <summary>
    /// The property <paramref name="name"/> resolves to, matched without regard to case — the way
    /// the body reader matches it. A name the reader binds must never be reported as undeclared, and
    /// it binds <c>label</c> to a property declared <c>Label</c>.
    /// </summary>
    private static IEdmProperty? Declared(IEdmStructuredType type, string name)
        => type.FindProperty(name)
           ?? type.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Lets a write body be read a second time, so <see cref="WriteBodyInspector"/> can look at it after
/// OData's reader has consumed it.
/// </summary>
/// <remarks>
/// A resource filter because model binding reads the body before any action filter runs. Only
/// <c>POST</c> and <c>PATCH</c> are buffered — the verbs whose body this controller binds.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
internal sealed class IyuBufferWriteBodyAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsPost(method) || HttpMethods.IsPatch(method))
            context.HttpContext.Request.EnableBuffering();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
