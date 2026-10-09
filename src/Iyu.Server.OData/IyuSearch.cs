using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace Iyu.Server.OData;

/// <summary>
/// The rule an entity set's <c>$search</c> applies, for an endpoint outside OData — an export, a template download, a
/// report or a bulk action that starts from «the rows the list shows for this search».
/// </summary>
/// <remarks>
/// <para>
/// One rule, not a second copy: the same parser (OData's <c>$search</c> grammar — words, quoted phrases, <c>AND</c>,
/// <c>OR</c>, <c>NOT</c>, juxtaposition as <c>AND</c>), the same properties (<see cref="Iyu.Core.Attributes.SearchableAttribute"/>
/// declarations, otherwise every readable string), the same step into each searchable reference, and the same
/// exclusion of a reference whose set's read policy the caller does not satisfy. A list and an endpoint fed the same
/// search text select the same rows of the read type.
/// </para>
/// <para>
/// This filters; it does not authorize. The caller's access to the read type itself is the endpoint's to enforce, as it
/// is for any query the endpoint runs.
/// </para>
/// <para>
/// Registered by the host. Resolve it from the request's services and pass the request's user.
/// </para>
/// </remarks>
public sealed class IyuSearch(IyuEntityPairRegistry registry, IAuthorizationService? authorization = null)
{
    /// <summary>
    /// The predicate <paramref name="search"/> selects on <typeparamref name="TRead"/> for <paramref name="user"/>, and the
    /// searchable references left out because the user may not read their set.
    /// </summary>
    /// <param name="search">The search text as a list sends it in <c>$search</c>. Empty or blank selects every row.</param>
    /// <param name="user">The caller — decides which policy-protected references are searched.</param>
    /// <exception cref="ArgumentException"><paramref name="search"/> is not a valid search expression.</exception>
    public async Task<IyuSearchFilter<TRead>> CreateFilterAsync<TRead>(string? search, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var parameter = Expression.Parameter(typeof(TRead), "x");
        if (string.IsNullOrWhiteSpace(search))
            return new(Expression.Lambda<Func<TRead, bool>>(Expression.Constant(true), parameter), []);

        var node = Parse(search);
        var allowed = await AllowedNavigationsAsync(typeof(TRead), user);
        var (followed, excluded) = IyuStringSearchBinder.Partition(registry, typeof(TRead), allowed);
        var body = IyuStringSearchBinder.BindNode(node, parameter, typeof(TRead), followed);
        return new(Expression.Lambda<Func<TRead, bool>>(body, parameter), excluded);
    }

    /// <summary>
    /// The searchable references of <paramref name="readType"/> that lead to a policy-protected set whose read policy
    /// <paramref name="user"/> satisfies. A reference to an unprotected set is searched without being listed; one to a
    /// protected set that is not listed is left out.
    /// </summary>
    public async Task<IReadOnlySet<string>> AllowedNavigationsAsync(Type readType, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(readType);
        ArgumentNullException.ThrowIfNull(user);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (authorization is null) return allowed;
        foreach (var navigation in IyuStringSearchBinder.SearchedNavigations(readType))
        {
            if (registry.FindByReadType(navigation.PropertyType) is not { ReadPolicy: { } policy }) continue;
            if ((await authorization.AuthorizeAsync(user, policy)).Succeeded) allowed.Add(navigation.Name);
        }
        return allowed;
    }

    private static QueryNode Parse(string search)
    {
        try
        {
            var parser = new ODataQueryOptionParser(
                EdmCoreModel.Instance, targetEdmType: null, targetNavigationSource: null,
                new Dictionary<string, string> { ["$search"] = search });
            return parser.ParseSearch().Expression;
        }
        catch (ODataException ex)
        {
            throw new ArgumentException($"Not a valid search expression: {ex.Message}", nameof(search), ex);
        }
    }
}

/// <summary>What <see cref="IyuSearch.CreateFilterAsync{TRead}"/> selects.</summary>
/// <param name="Predicate">
/// The rows the search selects — apply it with <c>Where</c>. Translatable by EF Core: a searchable reference becomes a
/// left join, and a row without the referenced row is matched by its own text only.
/// </param>
/// <param name="ExcludedNavigations">
/// The searchable references left out for this caller, in ordinal order — what a list names in its
/// <see cref="IyuStringSearchBinder.ExcludedHeader"/> response header.
/// </param>
public sealed record IyuSearchFilter<TRead>(Expression<Func<TRead, bool>> Predicate, IReadOnlyList<string> ExcludedNavigations);
