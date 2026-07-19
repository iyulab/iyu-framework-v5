using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.UriParser;

namespace Iyu.Server.OData;

/// <summary>
/// Default <see cref="ISearchBinder"/> for the Iyu runtime: a single
/// <c>$search="term"</c> matches when <em>any</em> readable string property of the
/// element contains the term (case-insensitive, DB-collation-independent).
/// </summary>
/// <remarks>
/// <para>
/// Without an <see cref="ISearchBinder"/> registered, ASP.NET Core OData silently
/// ignores <c>$search</c> (returns the full set), which surfaces to operators as
/// "search does nothing". Registering this binder per route component gives every
/// entity set a sensible default free-text search across its string columns.
/// </para>
/// <para>
/// Boolean search expressions are decomposed: <c>$search="a" AND "b"</c> becomes
/// <c>(any-prop contains a) AND (any-prop contains b)</c>, and <c>OR</c> / <c>NOT</c>
/// combine the same way. This matches the semantics a user expects when typing
/// several words into a search box (OData parses space-separated words as an
/// implicit <c>AND</c>). An expression node kind outside
/// term / <c>AND</c> / <c>OR</c> / <c>NOT</c> conservatively matches nothing —
/// returning everything would be worse — but no such kind exists in OData 4.01
/// <c>$search</c>, so that branch is unreachable in practice.
/// </para>
/// <para>
/// The generated predicate is EF-Core translatable:
/// <c>x =&gt; (x.P1 != null &amp;&amp; x.P1.ToLower().Contains(term)) || ...</c> →
/// <c>WHERE LOWER([P1]) LIKE '%term%' OR ...</c>. <c>ToLower()</c> on both sides keeps
/// matching case-insensitive regardless of the database collation.
/// </para>
/// </remarks>
public sealed class IyuStringSearchBinder : ISearchBinder
{
    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    private static readonly MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    /// <inheritdoc />
    public Expression BindSearch(SearchClause searchClause, QueryBinderContext context)
    {
        ArgumentNullException.ThrowIfNull(searchClause);
        ArgumentNullException.ThrowIfNull(context);

        var parameter = context.CurrentParameter;

        // The whole clause is bound into a single body over one shared parameter, then
        // wrapped in exactly one lambda — nesting lambdas per node would not be
        // EF-Core translatable.
        var body = BindNode(searchClause.Expression, parameter, context.ElementClrType);

        return Expression.Lambda(body, parameter);
    }

    private static Expression BindNode(QueryNode? node, ParameterExpression parameter, Type elementClrType)
        => node switch
        {
            SearchTermNode term => BindTerm(term, parameter, elementClrType),

            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } and_ => Expression.AndAlso(
                BindNode(and_.Left, parameter, elementClrType),
                BindNode(and_.Right, parameter, elementClrType)),

            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } or_ => Expression.OrElse(
                BindNode(or_.Left, parameter, elementClrType),
                BindNode(or_.Right, parameter, elementClrType)),

            UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not => Expression.Not(
                BindNode(not.Operand, parameter, elementClrType)),

            // Unreachable for OData 4.01 $search (term / AND / OR / NOT is the whole
            // grammar); matching nothing stays the conservative choice if that changes.
            _ => Expression.Constant(false),
        };

    private static Expression BindTerm(SearchTermNode termNode, ParameterExpression parameter, Type elementClrType)
    {
        if (string.IsNullOrEmpty(termNode.Text))
        {
            return Expression.Constant(false);
        }

        var term = Expression.Constant(termNode.Text.ToLowerInvariant(), typeof(string));

        var stringProperties = elementClrType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();

        Expression? body = null;
        foreach (var property in stringProperties)
        {
            var access = Expression.Property(parameter, property);
            var notNull = Expression.NotEqual(access, Expression.Constant(null, typeof(string)));
            var lowered = Expression.Call(access, ToLowerMethod);
            var contains = Expression.Call(lowered, ContainsMethod, term);
            var clause = Expression.AndAlso(notNull, contains);
            body = body is null ? clause : Expression.OrElse(body, clause);
        }

        return body ?? Expression.Constant(false);
    }
}
