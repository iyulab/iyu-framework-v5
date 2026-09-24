using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace Iyu.Server.OData;

/// <summary>
/// Finds the property names a write body carries that the entity set's type does not declare.
/// </summary>
/// <remarks>
/// <para>
/// OData's body reader refuses such a name by throwing, and the only trace it leaves is the
/// exception's message — text that also carries the EDM type's full name, and whose wording belongs
/// to the package, not to this framework. Recovering the name from that text would tie the answer to
/// a string a package upgrade may reword. Comparing the body against the model instead needs only
/// what this framework already owns: the EDM type of the set the request addresses, and the body
/// the caller sent.
/// </para>
/// <para>
/// It runs only after the bind has already failed, so a request that binds pays nothing but the
/// buffering <see cref="IyuBufferWriteBodyAttribute"/> sets up.
/// </para>
/// </remarks>
internal static class UndeclaredBodyProperties
{
    /// <summary>
    /// The undeclared names in the request body, each as the path a caller would write it
    /// (<c>Name</c>, <c>Address.Citty</c>) — or an empty list when the body cannot be re-read, is
    /// not a JSON object, or addresses no entity set.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.Body.CanSeek) return [];
        var type = request.ODataFeature().Path?.OfType<EntitySetSegment>().FirstOrDefault()?.EntitySet.EntityType;
        if (type is null) return [];

        request.Body.Position = 0;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var found = new List<string>();
            Collect(document.RootElement, type, prefix: "", found);
            return found;
        }
    }

    private static void Collect(JsonElement value, IEdmStructuredType type, string prefix, List<string> found)
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
                found.Add(path);
                continue;
            }

            if (ElementStructuredType(property.Type) is not { } nested) continue;
            if (member.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in member.Value.EnumerateArray())
                    Collect(item, nested, path + ".", found);
            }
            else
            {
                Collect(member.Value, nested, path + ".", found);
            }
        }
    }

    /// <summary>
    /// The property <paramref name="name"/> resolves to, matched without regard to case — the way
    /// the body reader matches it. A name the reader binds must never be reported here, and it binds
    /// <c>label</c> to a property declared <c>Label</c>.
    /// </summary>
    private static IEdmProperty? Declared(IEdmStructuredType type, string name)
        => type.FindProperty(name)
           ?? type.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IEdmStructuredType? ElementStructuredType(IEdmTypeReference type)
        => type.IsCollection()
            ? type.AsCollection().ElementType().Definition as IEdmStructuredType
            : type.Definition as IEdmStructuredType;
}

/// <summary>
/// Lets a write body be read a second time, so <see cref="UndeclaredBodyProperties"/> can look at it
/// after OData's reader has consumed it.
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
