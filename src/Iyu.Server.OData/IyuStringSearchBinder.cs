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
/// Only a simple single-term search is supported (the common
/// <c>$search="abc"</c> case). Boolean search expressions
/// (<c>$search="a AND b"</c>) fall back to matching the raw composite text and are
/// not decomposed — consumers needing richer semantics should use <c>$filter</c>.
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

        // Only plain single-term search is handled; anything else → match nothing
        // (safer than silently returning everything, which is the bug this fixes).
        if (searchClause.Expression is not SearchTermNode termNode
            || string.IsNullOrEmpty(termNode.Text))
        {
            return Expression.Lambda(Expression.Constant(false), parameter);
        }

        var term = Expression.Constant(termNode.Text.ToLowerInvariant(), typeof(string));

        var stringProperties = context.ElementClrType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();

        if (stringProperties.Count == 0)
        {
            return Expression.Lambda(Expression.Constant(false), parameter);
        }

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

        return Expression.Lambda(body!, parameter);
    }
}
