using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Iyu.Core.Attributes;
using Microsoft.AspNetCore.Http;
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
/// <para>
/// <b>Which properties.</b> A type that marks properties with <see cref="SearchableAttribute"/> is
/// searched across those only; a type that marks none, across every readable string property. The
/// declaration is what keeps a note, a share token or an identification number from matching a
/// search meant for names — and fewer columns means fewer <c>LIKE</c> clauses per term.
/// </para>
/// <para>
/// <b>One step into a reference.</b> A <see cref="SearchableAttribute"/> on a reference navigation (a single
/// related read type, not a collection) searches that type's searched string properties too — a list shown
/// with its reference's name is found by that name. One step only: the related type's own navigations are not
/// followed. A navigation to a type no entity set serves is not searched, and neither is one the request has
/// been refused for (<see cref="ExcludedNavigationsItem"/>) — a caller who may not read the related set must not
/// learn its rows by filtering on them.
/// </para>
/// </remarks>
public sealed class IyuStringSearchBinder(IyuEntityPairRegistry? registry = null, IHttpContextAccessor? httpContext = null)
    : ISearchBinder
{
    /// <summary>
    /// The <see cref="HttpContext.Items"/> key under which a request lists, as a set of property names, the
    /// searchable navigations it may not search — set by the host's authorization before the query is bound.
    /// </summary>
    public const string ExcludedNavigationsItem = "Iyu.Search.ExcludedNavigations";

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
        var excluded = httpContext?.HttpContext?.Items[ExcludedNavigationsItem] as IReadOnlySet<string>;
        var navigations = SearchedNavigations(context.ElementClrType)
            .Where(n => registry?.FindByReadType(n.PropertyType) is not null)
            .Where(n => excluded is null || !excluded.Contains(n.Name))
            .ToList();
        var body = BindNode(searchClause.Expression, parameter, context.ElementClrType, navigations);

        return Expression.Lambda(body, parameter);
    }

    private static Expression BindNode(
        QueryNode? node, ParameterExpression parameter, Type elementClrType, IReadOnlyList<PropertyInfo> navigations)
        => node switch
        {
            SearchTermNode term => BindTerm(term, parameter, elementClrType, navigations),

            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } and_ => Expression.AndAlso(
                BindNode(and_.Left, parameter, elementClrType, navigations),
                BindNode(and_.Right, parameter, elementClrType, navigations)),

            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } or_ => Expression.OrElse(
                BindNode(or_.Left, parameter, elementClrType, navigations),
                BindNode(or_.Right, parameter, elementClrType, navigations)),

            UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not => Expression.Not(
                BindNode(not.Operand, parameter, elementClrType, navigations)),

            // Unreachable for OData 4.01 $search (term / AND / OR / NOT is the whole
            // grammar); matching nothing stays the conservative choice if that changes.
            _ => Expression.Constant(false),
        };

    private static Expression BindTerm(
        SearchTermNode termNode, ParameterExpression parameter, Type elementClrType, IReadOnlyList<PropertyInfo> navigations)
    {
        if (string.IsNullOrEmpty(termNode.Text))
        {
            return Expression.Constant(false);
        }

        var term = Expression.Constant(termNode.Text.ToLowerInvariant(), typeof(string));

        Expression? body = null;
        foreach (var property in SearchedProperties(elementClrType))
            body = Or(body, Matches(Expression.Property(parameter, property), term));

        // One step into each searchable reference: `x.Nav != null && <Nav's text matches>` — a left join.
        foreach (var navigation in navigations)
        {
            var related = Expression.Property(parameter, navigation);
            Expression? relatedBody = null;
            foreach (var property in SearchedProperties(navigation.PropertyType))
                relatedBody = Or(relatedBody, Matches(Expression.Property(related, property), term));
            if (relatedBody is not null)
                body = Or(body, Expression.AndAlso(Expression.NotEqual(related, Expression.Constant(null, navigation.PropertyType)), relatedBody));
        }

        return body ?? Expression.Constant(false);

        static Expression Or(Expression? left, Expression right) => left is null ? right : Expression.OrElse(left, right);
    }

    private static Expression Matches(Expression access, ConstantExpression term)
    {
        var notNull = Expression.NotEqual(access, Expression.Constant(null, typeof(string)));
        var lowered = Expression.Call(access, ToLowerMethod);
        return Expression.AndAlso(notNull, Expression.Call(lowered, ContainsMethod, term));
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Searched = new();

    /// <summary>
    /// The string properties a term is matched against: the ones marked
    /// <see cref="SearchableAttribute"/> when the type marks any property at all, otherwise every
    /// readable string property.
    /// </summary>
    /// <remarks>
    /// The declaration is read from the element type the query runs over — the set's read type —
    /// including properties it inherits. A declaration on a non-string property narrows the search
    /// all the same but contributes no clause, since search compares text.
    /// </remarks>
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> SearchedReferences = new();

    /// <summary>
    /// The reference navigations marked <see cref="SearchableAttribute"/> — a single related object, not a string,
    /// a value type or a collection — whose related type's text a term is also matched against.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> SearchedNavigations(Type elementClrType) => SearchedReferences.GetOrAdd(elementClrType, static type =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => p.IsDefined(typeof(SearchableAttribute), inherit: true))
            .Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))
            .ToArray());

    internal static PropertyInfo[] SearchedProperties(Type elementClrType) => Searched.GetOrAdd(elementClrType, static type =>
    {
        var readable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();
        var declared = readable.Where(p => p.IsDefined(typeof(SearchableAttribute), inherit: true)).ToList();
        return (declared.Count > 0 ? declared : readable)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();
    });
}
