using Microsoft.Extensions.DependencyInjection;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.OData;
using Microsoft.OData.UriParser;

namespace Iyu.MainServer;

/// <summary>
/// Applies an entity set's <c>ReadPolicy</c> to every set a request reaches through
/// <c>$expand</c>, not only to the set it addressed.
/// </summary>
/// <remarks>
/// <para>
/// <c>RestrictPolicy</c> is enforced by an <c>AuthorizeFilter</c> on the restricted set's own
/// controller action (<see cref="IyuODataAuthorizationConvention"/>). An expand is served by the
/// <em>addressed</em> set's action and never enters the other one, so that filter has no
/// opportunity to run — which makes the declaration a restriction on one route to the data rather
/// than on the data. Measured: an anonymous caller that gets 401 on the protected set directly
/// received the protected rows inline when it expanded into them from an open set.
/// </para>
/// <para>
/// The check runs before the action, so a refusal costs no query. It is deliberately
/// <b>fail-closed</b> on a <c>$expand</c> this filter cannot parse: an expression that this parser
/// rejects and the query pipeline later accepts would otherwise be a way through, and the same
/// reasoning made <c>AddIyuWriteRules</c> refuse an assembly it cannot fully load rather than
/// register the part of it that loaded.
/// </para>
/// <para>
/// Nested expands are walked to the bottom. Depth itself is bounded by
/// <c>EnableQueryAttribute</c>'s documented default of two, which this framework does not override.
/// </para>
/// </remarks>
internal sealed class IyuExpandAuthorizationFilter(string setName) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var raw = context.HttpContext.Request.Query["$expand"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            await next();
            return;
        }

        var services = context.HttpContext.RequestServices;
        var registry = services.GetService<IyuEntityPairRegistry>();
        var authorization = services.GetService<IAuthorizationService>();
        var model = context.HttpContext.ODataFeature().Model;

        // Nothing to enforce against, or nothing to enforce with. Not a silent pass: the filter is
        // only ever attached when some registered set carries a policy, so this means the request
        // arrived outside the pipeline the attachment assumed, and the addressed action's own
        // AuthorizeFilter still applies.
        if (registry is null || authorization is null || model is null)
        {
            await next();
            return;
        }

        IReadOnlyCollection<string> expanded;
        try
        {
            expanded = ExpandedSetNames(model, setName, raw);
        }
        catch (ODataException)
        {
            context.Result = new BadRequestObjectResult(
                "The $expand expression could not be interpreted and was refused. " +
                "Expand each navigation property by name, without a nested option this server " +
                "does not accept.");
            return;
        }

        foreach (var target in expanded)
        {
            var policy = registry.Find(target)?.ReadPolicy;
            if (policy is null) continue;

            var result = await authorization.AuthorizeAsync(context.HttpContext.User, policy);
            if (result.Succeeded) continue;

            // Same split the framework's own AuthorizeFilter produces: a caller with no identity is
            // told to authenticate, one with an identity is told it is not enough.
            context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
                ? new ForbidResult()
                : new ChallengeResult();
            return;
        }

        await next();
    }

    /// <summary>
    /// Parses <paramref name="rawExpand"/> against the EDM and returns the name of every entity set
    /// it reaches, at any depth. The parsed clause names its target navigation source directly, so
    /// no mapping from property name to set has to be reinvented here.
    /// </summary>
    private static IReadOnlyCollection<string> ExpandedSetNames(
        Microsoft.OData.Edm.IEdmModel model, string setName, string rawExpand)
    {
        var entitySet = model.EntityContainer?.FindEntitySet(setName);
        if (entitySet is null)
            throw new ODataException($"Entity set '{setName}' is not in the model.");

        var parser = new ODataQueryOptionParser(
            model,
            entitySet.EntityType,
            entitySet,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["$expand"] = rawExpand });

        var names = new HashSet<string>(StringComparer.Ordinal);
        Walk(parser.ParseSelectAndExpand(), names);
        return names;
    }

    private static void Walk(SelectExpandClause? clause, HashSet<string> into)
    {
        if (clause is null) return;

        foreach (var item in clause.SelectedItems.OfType<ExpandedReferenceSelectItem>())
        {
            if (item.NavigationSource is { } source) into.Add(source.Name);
            if (item is ExpandedNavigationSelectItem navigation) Walk(navigation.SelectAndExpand, into);
        }
    }
}
