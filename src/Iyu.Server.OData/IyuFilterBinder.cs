using System.Linq.Expressions;
using Microsoft.AspNetCore.OData.Edm;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace Iyu.Server.OData;

/// <summary>
/// The Iyu runtime's <see cref="IFilterBinder"/>: ASP.NET Core OData's own binder, with the
/// <c>in</c> operator resolving enum values the same way <c>eq</c> does.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IyuEdmModelBuilder"/> names each EDM enum member after its
/// <c>[EnumMember(Value = ...)]</c> wire value, so a client filters with that spelling. The stock
/// binder resolves a single constant (<c>Kind eq 'print_order'</c>) through the model's
/// EDM-to-CLR member map, but resolves each item of a collection constant
/// (<c>Kind in ('product','print_order')</c>) with <c>Enum.Parse</c> on the EDM name — which
/// is the wire value, not the CLR member name — and throws, surfacing a valid filter as a 500.
/// </para>
/// <para>
/// This binder rewrites each enum item of a collection constant to its CLR member name through
/// the same map, then hands the node back to the stock binding, so parameterization and
/// nullable handling stay exactly as the base class implements them. Items the map does not
/// resolve are passed through unchanged; the URI parser has already rejected undeclared values
/// with a 400 before binding starts.
/// </para>
/// <para>
/// TODO(upstream): remove this override once <c>QueryBinder.BindCollectionConstantNode</c> in
/// Microsoft.AspNetCore.OData maps enum items through <c>ClrEnumMemberAnnotation</c> like
/// <c>BindConstantNode</c> already does (unchanged through 9.5.0).
/// </para>
/// </remarks>
public sealed class IyuFilterBinder : FilterBinder
{
    /// <inheritdoc />
    public override Expression BindCollectionConstantNode(CollectionConstantNode node, QueryBinderContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        return base.BindCollectionConstantNode(MapEnumItemsToClrNames(node, context.Model), context);
    }

    private static CollectionConstantNode MapEnumItemsToClrNames(CollectionConstantNode node, IEdmModel model)
    {
        if (node.ItemType is not { } itemType || !itemType.IsEnum()) return node;

        var enumType = itemType.AsEnum().EnumDefinition();
        var memberMap = model.GetClrEnumMemberAnnotation(enumType);
        if (memberMap is null) return node;

        var changed = false;
        var items = new List<object?>(node.Collection.Count);
        foreach (var item in node.Collection)
        {
            if (item.Value is ODataEnumValue enumValue
                && FindMember(enumType, enumValue.Value) is { } member
                && memberMap.GetClrEnumMember(member) is { } clrMember)
            {
                var clrName = clrMember.ToString();
                changed |= !string.Equals(clrName, enumValue.Value, StringComparison.Ordinal);
                items.Add(new ODataEnumValue(clrName, enumValue.TypeName));
            }
            else
            {
                items.Add(item.Value);
            }
        }

        return changed ? new CollectionConstantNode(items, node.LiteralText, node.CollectionType) : node;
    }

    private static IEdmEnumMember? FindMember(IEdmEnumType enumType, string value) =>
        enumType.Members.FirstOrDefault(m => string.Equals(m.Name, value, StringComparison.Ordinal))
        ?? enumType.Members.FirstOrDefault(m => string.Equals(
            m.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), value, StringComparison.Ordinal));
}
