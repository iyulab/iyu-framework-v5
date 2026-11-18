using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Iyu.Core.Attributes;
using Iyu.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// followed. When the request's <see cref="IyuDbContext"/> maps the reference as a foreign key, the reference's text is
/// tested by a correlated <c>EXISTS</c> over the related set — not by a left join, which a database can only answer by
/// reading the related row for every candidate before it filters, orders or pages (a read model with computed columns
/// then pays for them on every row). Without a context, or for a reference the model does not map that way, the
/// reference is reached as a left join. A navigation to a type no entity set serves is not searched. One whose set carries a read policy is
/// searched only when the request lists it as allowed (<see cref="AllowedNavigationsItem"/>, which the host's
/// authorization fills) — denied by default, so a caller who may not read the related set cannot learn its rows by
/// filtering on them however the search reached this binder. What was left out is named in
/// <see cref="ExcludedHeader"/>.
/// </para>
/// </remarks>
public sealed class IyuStringSearchBinder(IyuEntityPairRegistry? registry = null, IHttpContextAccessor? httpContext = null)
    : ISearchBinder
{
    /// <summary>
    /// The <see cref="HttpContext.Items"/> key under which a request lists the searchable navigations to a
    /// policy-protected set that its caller may search — filled by the host's authorization before the query is
    /// bound. The value is an <see cref="IReadOnlyDictionary{TKey,TValue}"/> from a read type to the names of that
    /// type's allowed navigations. A protected navigation not listed <em>for the type being searched</em> is left out.
    /// </summary>
    /// <remarks>
    /// Keyed by type because a name says nothing on its own: two read types may each have a reference called
    /// <c>Owner</c> that lead to different sets, and a search nested in an expand binds a type other than the one
    /// the request addressed. Permission to search one type's <c>Owner</c> is not permission to search another's.
    /// </remarks>
    public const string AllowedNavigationsItem = "Iyu.Search.AllowedNavigations";

    /// <summary>The response header naming the searchable navigations a search left out for this caller.</summary>
    public const string ExcludedHeader = "Iyu-Search-Excluded";

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
        var http = httpContext?.HttpContext;
        // What the request allows for this type, and only for this type. Nothing listed for it — a type reached by
        // a nested search, say — is nothing allowed.
        var allowed = (http?.Items[AllowedNavigationsItem] as IReadOnlyDictionary<Type, IReadOnlySet<string>>)
            ?.GetValueOrDefault(context.ElementClrType);
        var (navigations, excluded) = Partition(registry, context.ElementClrType, allowed);
        if (excluded.Count > 0 && http is { Response.HasStarted: false })
            http.Response.Headers[ExcludedHeader] = string.Join(", ", excluded);

        var db = http?.RequestServices?.GetService<IyuDbContext>();
        var body = BindNode(searchClause.Expression, parameter, context.ElementClrType, navigations, db);

        return Expression.Lambda(body, parameter);
    }

    /// <summary>
    /// The searchable navigations of <paramref name="elementType"/> a search follows, and the names of those it leaves
    /// out (ordinal order): a navigation to a type no set serves is neither; one to a set with a read policy is followed
    /// only when <paramref name="allowed"/> lists it — denied by default.
    /// </summary>
    internal static (IReadOnlyList<PropertyInfo> Followed, IReadOnlyList<string> Excluded) Partition(
        IyuEntityPairRegistry? registry, Type elementType, IReadOnlySet<string>? allowed)
    {
        var followed = new List<PropertyInfo>();
        var excluded = new List<string>();
        foreach (var navigation in SearchedNavigations(elementType))
        {
            if (registry?.FindByReadType(navigation.PropertyType) is not { } target) continue;
            if (!IsProtected(target) || allowed?.Contains(navigation.Name) == true) followed.Add(navigation);
            else excluded.Add(navigation.Name);
        }
        excluded.Sort(StringComparer.Ordinal);
        return (followed, excluded);
    }

    /// <summary>
    /// Whether searching a set's text needs the caller to satisfy something: the set's own read policy, or the read policy
    /// of one of its properties (a search tests the value of every property it matches).
    /// </summary>
    internal static bool IsProtected(IyuEntityPairRegistry.EntityPair target)
        => target.ReadPolicy is not null || target.PropertyReadPolicies.Count > 0;

    internal static Expression BindNode(
        QueryNode? node, ParameterExpression parameter, Type elementClrType, IReadOnlyList<PropertyInfo> navigations,
        DbContext? db = null)
        => node switch
        {
            SearchTermNode term => BindTerm(term, parameter, elementClrType, navigations, db),

            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And } and_ => Expression.AndAlso(
                BindNode(and_.Left, parameter, elementClrType, navigations, db),
                BindNode(and_.Right, parameter, elementClrType, navigations, db)),

            BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Or } or_ => Expression.OrElse(
                BindNode(or_.Left, parameter, elementClrType, navigations, db),
                BindNode(or_.Right, parameter, elementClrType, navigations, db)),

            UnaryOperatorNode { OperatorKind: UnaryOperatorKind.Not } not => Expression.Not(
                BindNode(not.Operand, parameter, elementClrType, navigations, db)),

            // Unreachable for OData 4.01 $search (term / AND / OR / NOT is the whole
            // grammar); matching nothing stays the conservative choice if that changes.
            _ => Expression.Constant(false),
        };

    private static Expression BindTerm(
        SearchTermNode termNode, ParameterExpression parameter, Type elementClrType, IReadOnlyList<PropertyInfo> navigations,
        DbContext? db)
    {
        if (string.IsNullOrEmpty(termNode.Text))
        {
            return Expression.Constant(false);
        }

        var term = Expression.Constant(termNode.Text.ToLowerInvariant(), typeof(string));

        Expression? body = null;
        foreach (var property in SearchedProperties(elementClrType))
            body = Or(body, Matches(Expression.Property(parameter, property), term));

        // One step into each searchable reference. Where the model maps it as a foreign key the text is tested in a correlated
        // EXISTS; otherwise `x.Nav != null && <Nav's text matches>`, which a database reads as a left join.
        foreach (var navigation in navigations)
        {
            if (db is not null && ExistsOverReference(db, elementClrType, navigation, parameter, term) is { } exists)
            {
                body = Or(body, exists);
                continue;
            }

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

    private static readonly MethodInfo SetMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;

    private static readonly MethodInfo AnyMethod = typeof(Queryable).GetMethods()
        .Single(m => m is { Name: nameof(Queryable.Any) } && m.GetParameters().Length == 2);

    /// <summary>
    /// <c>db.Set&lt;Related&gt;().Any(r =&gt; r.Key == x.Key &amp;&amp; &lt;r's text matches&gt;)</c> for a reference the model maps as a foreign
    /// key, or <see langword="null"/> when it does not — an element type or target the model does not know, a key
    /// without a CLR property, a key whose two sides cannot be compared — and the caller falls back to the left join.
    /// </summary>
    /// <remarks>
    /// A row with no referenced row matches nothing here, which is what the left join's null check said. Query filters of the
    /// related type apply to the subquery as they did to the join.
    /// </remarks>
    private static Expression? ExistsOverReference(
        DbContext db, Type elementClrType, PropertyInfo navigation, ParameterExpression parameter, ConstantExpression term)
    {
        if (db.Model.FindEntityType(elementClrType)?.FindNavigation(navigation.Name) is not { } mapped) return null;
        if (mapped.TargetEntityType.ClrType != navigation.PropertyType) return null;

        var foreignKey = mapped.ForeignKey;
        var own = mapped.IsOnDependent ? foreignKey.Properties : foreignKey.PrincipalKey.Properties;
        var other = mapped.IsOnDependent ? foreignKey.PrincipalKey.Properties : foreignKey.Properties;
        if (own.Count != other.Count || own.Any(p => p.PropertyInfo is null) || other.Any(p => p.PropertyInfo is null)) return null;

        var related = Expression.Parameter(navigation.PropertyType, "r");

        Expression? text = null;
        foreach (var property in SearchedProperties(navigation.PropertyType))
        {
            var match = Matches(Expression.Property(related, property), term);
            text = text is null ? match : Expression.OrElse(text, match);
        }
        if (text is null) return null;

        Expression? correlation = null;
        for (var i = 0; i < own.Count; i++)
        {
            var mine = Expression.Property(parameter, own[i].PropertyInfo!);
            var theirs = Expression.Property(related, other[i].PropertyInfo!);
            var (left, right) = SameType(mine, theirs);
            if (left is null || right is null) return null;
            var equal = Expression.Equal(left, right);
            correlation = correlation is null ? equal : Expression.AndAlso(correlation, equal);
        }
        if (correlation is null) return null;

        var root = Expression.Call(Expression.Constant(db), SetMethod.MakeGenericMethod(navigation.PropertyType));
        var predicate = Expression.Lambda(Expression.AndAlso(correlation, text), related);
        return Expression.Call(AnyMethod.MakeGenericMethod(navigation.PropertyType), root, Expression.Quote(predicate));
    }

    /// <summary>Two key accesses of the same underlying type as one comparable type (a nullable key against its principal).</summary>
    private static (Expression? Left, Expression? Right) SameType(Expression left, Expression right)
    {
        if (left.Type == right.Type) return (left, right);
        var underlying = Nullable.GetUnderlyingType(left.Type) ?? left.Type;
        if (underlying != (Nullable.GetUnderlyingType(right.Type) ?? right.Type) || !underlying.IsValueType) return (null, null);
        var lifted = typeof(Nullable<>).MakeGenericType(underlying);
        return (Expression.Convert(left, lifted), Expression.Convert(right, lifted));
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
            .Where(IsDeclaredSearchable)
            .Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))
            .ToArray());

    internal static PropertyInfo[] SearchedProperties(Type elementClrType) => Searched.GetOrAdd(elementClrType, static type =>
    {
        var readable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();
        var declared = readable.Where(IsDeclaredSearchable).ToList();
        return (declared.Count > 0 ? declared : readable)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();
    });

    /// <summary>
    /// Whether <paramref name="property"/> carries <see cref="SearchableAttribute"/>, on itself or on the property it
    /// overrides. <see cref="Attribute.IsDefined(MemberInfo, Type, bool)"/>, not <c>PropertyInfo.IsDefined</c>: the
    /// latter ignores <c>inherit</c> for properties, and an override that does not repeat the declaration would leave
    /// its type declaring nothing — searched across every string.
    /// </summary>
    private static bool IsDeclaredSearchable(PropertyInfo property)
        => Attribute.IsDefined(property, typeof(SearchableAttribute), inherit: true);
}
