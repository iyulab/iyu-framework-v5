using Microsoft.Extensions.DependencyInjection;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using Microsoft.OData.UriParser.Aggregation;

namespace Iyu.MainServer;

/// <summary>
/// Applies an entity set's <c>ReadPolicy</c> to every set a request reaches through a navigation — in
/// whichever query option names it — not only to the set the request addressed.
/// </summary>
/// <remarks>
/// <para>
/// <c>RestrictPolicy</c> is enforced by an <c>AuthorizeFilter</c> on the restricted set's own
/// controller action (<see cref="IyuODataAuthorizationConvention"/>). A request that reaches that set
/// through a navigation is served by the <em>addressed</em> set's action and never enters the other
/// one, so that filter has no opportunity to run — which makes the declaration a restriction on one
/// route to the data rather than on the data. Measured, twice: an anonymous caller that gets 401 on
/// the protected set directly received the protected rows inline when it expanded into them from an
/// open set; and with expands checked, a caller without the target's policy still read the target's
/// values with <c>$apply=groupby((Nav/Prop))</c>, and could test them one guess at a time with
/// <c>$filter=Nav/Prop eq '…'</c> and a count.
/// </para>
/// <para>
/// So the unit is the navigation, not the option. Every option that can name a path is parsed —
/// <c>$expand</c>, <c>$select</c>, <c>$filter</c>, <c>$orderby</c>, <c>$compute</c>, <c>$apply</c>, the
/// options nested inside an expand or a select item, and the values of parameter aliases — and each
/// navigation found, at any depth, names a set whose read policy the caller must satisfy. Reading a
/// value, grouping by it, sorting by it and testing it are all reading it.
/// </para>
/// <para>
/// The check runs before the action, so a refusal costs no query. It is deliberately
/// <b>fail-closed</b>: an expression this filter cannot parse, a construct it has no rule for, and an
/// option given twice are refused. An expression that this parser rejects and the query pipeline
/// later accepts would otherwise be a way through, and the same reasoning made
/// <c>AddIyuWriteRules</c> refuse an assembly it cannot fully load rather than register the part of
/// it that loaded.
/// </para>
/// <para>
/// Nested expands are walked to the bottom. Depth itself is bounded by
/// <c>EnableQueryAttribute</c>'s documented default of two, which this framework does not override.
/// </para>
/// </remarks>
internal sealed class IyuNavigationAuthorizationFilter(string setName) : IAsyncResourceFilter
{
    /// <summary>The query options that can name a navigation path, in the order they are parsed.</summary>
    private static readonly string[] PathOptions = ["apply", "compute", "filter", "orderby", "select", "expand"];

    // A resource filter, not an action filter: it runs before the query options are bound. Binding is where the
    // pipeline first reads them, and a request it cannot bind (the same option under both spellings) fails there
    // with an unhandled error — the refusal has to come first, and it has to be this filter's.
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var searchAllowed = await AllowReadableSearchReferencesAsync(context.HttpContext);

        var request = context.HttpContext.Request;
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in PathOptions)
        {
            var values = OptionValues(request, option);
            if (values.Count == 0) continue;
            if (values.Count > 1)
            {
                // Under both spellings. The pipeline would apply one of them; which one is not this filter's to
                // guess, and the one it did not check would be the way through.
                context.Result = Refusal($"The query option ${option} is given more than once — with and without "
                                         + "the '$'. Give it once.");
                return;
            }
            given["$" + option] = values[0];
        }

        var services = context.HttpContext.RequestServices;
        var registry = services.GetService<IyuEntityPairRegistry>();
        var model = context.HttpContext.ODataFeature().Model;

        // Nothing to work the reach out against. Not a silent pass: the filter is only ever attached
        // through AddIyuMainServer, which registers both, so this means the request arrived outside the
        // pipeline the attachment assumed, and the addressed action's own AuthorizeFilter still applies.
        if (registry is null || model is null)
        {
            await next();
            return;
        }

        // A parameter alias stands for an expression, which may be a path: @p=Nav/Prop.
        foreach (var alias in request.Query.Where(q => q.Key.StartsWith('@')))
            given[alias.Key] = alias.Value.ToString();

        Reach reach;
        try
        {
            reach = given.Count == 0 ? new Reach() : ReachedSets(model, setName, given, request.GetRouteServices());
        }
        catch (UninterpretedQueryException refused)
        {
            context.Result = Refusal(refused.Message);
            return;
        }

        // Every navigation the model keeps is bound to a registered set — navigations to types no
        // set serves are dropped when the model is built. One that still reaches no registered set
        // has no read policy to apply, and allowing it would serve rows nothing declared a policy
        // for; refuse it rather than treating "no set" like "an open set".
        if (reach.Sets.Any(target => registry.Find(target) is null))
        {
            context.Result = Refusal("The query reaches data no entity set of this API serves and was refused.");
            return;
        }

        // Published before any policy is evaluated: a refused request is a record of what was tried.
        if (OptionValues(request, "search").Count > 0) reach.Searched.Add(setName);
        context.HttpContext.Features.Set<IIyuODataReachFeature>(new IyuODataReachFeature(
            setName, PublishedReach(registry, setName, reach, searchAllowed)));

        if (services.GetService<IAuthorizationService>() is not { } authorization)
        {
            // Attached for the reach alone (ComputeODataReach) on a host with nothing to authorize with.
            await next();
            return;
        }

        foreach (var target in reach.Sets)
        {
            var policy = registry.Find(target)!.ReadPolicy;
            if (policy is null) continue;

            var result = await authorization.AuthorizeAsync(context.HttpContext.User, policy);
            if (result.Succeeded) continue;

            // The endpoint's metadata names the addressed set's policy, not this one — hand on the
            // one that actually failed for a host that words its own refusals.
            context.HttpContext.Features.Set<IIyuAuthorizationFailureFeature>(
                new IyuAuthorizationFailureFeature(policy, target));

            // Same split the framework's own AuthorizeFilter produces: a caller with no identity is
            // told to authenticate, one with an identity is told it is not enough.
            context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
                ? new ForbidResult()
                : new ChallengeResult();
            return;
        }

        await next();
    }

    private static ObjectResult Refusal(string message)
        => new(new ODataError { Code = ODataErrorCodes.InvalidQuery, Message = message })
        { StatusCode = StatusCodes.Status400BadRequest };

    /// <summary>
    /// What the request gives the query option <paramref name="name"/>, one value per spelling the OData query
    /// pipeline accepts: with or without the <c>$</c> (letter case does not make another spelling — query keys are
    /// compared without regard to it).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check has to see what the pipeline will apply. Reading only the literal <c>$expand</c> parameter let
    /// <c>expand=Secret</c> — which the pipeline applies just the same — carry a protected set's rows past it.
    /// </para>
    /// <para>
    /// An option repeated under one spelling is one value: the pipeline reads <c>$orderby=A&amp;$orderby=B</c> as
    /// <c>A,B</c>, so that is what is checked.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> OptionValues(HttpRequest request, string name)
        => request.Query
            .Where(q => string.Equals(q.Key.TrimStart('$'), name, StringComparison.OrdinalIgnoreCase))
            .Select(q => q.Value.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

    /// <summary>
    /// A search also matches the text of each searchable reference (<c>IyuStringSearchBinder</c>), so a caller who may
    /// not read the referenced set would learn its rows by filtering on them. The binder therefore leaves out every
    /// reference to a policy-protected set unless the request lists it as allowed — this fills that list with the
    /// references whose read policy the caller satisfies. Nothing listed (services missing, policy not met) means
    /// left out: the default is deny.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed for every request rather than keyed on the query string: OData accepts the search option in more than
    /// one spelling (with or without <c>$</c>, or in a <c>$query</c> body), and the binder is what sees all of them.
    /// </para>
    /// <para>
    /// Left out, not refused: unlike a path the caller wrote, which names the set it reaches, a searchable reference
    /// is the server's declaration — refusing the whole search would take search away from everyone who may not read
    /// one referenced set. The binder names what it left out in a response header.
    /// </para>
    /// <para>
    /// Returns what it listed — <see langword="null"/> when it listed nothing — so the published reach is worked out
    /// from the list the binder will read, not from a second evaluation of the same policies.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlySet<string>?> AllowReadableSearchReferencesAsync(HttpContext http)
    {
        var services = http.RequestServices;
        if (services.GetService<IyuEntityPairRegistry>() is not { } registry
            || services.GetService<IyuSearch>() is not { } search
            || registry.Find(setName) is not { } addressed) return null;

        // The same decision IyuSearch makes for an endpoint outside OData — one home for «which references may this
        // caller search».
        // Listed for the addressed type alone. A search that binds another type (one nested in an expand) finds
        // nothing listed for it, and leaves that type's protected references out.
        var allowed = await search.AllowedNavigationsAsync(addressed.ReadType, http.User);
        http.Items[IyuStringSearchBinder.AllowedNavigationsItem] = new Dictionary<Type, IReadOnlySet<string>>
        {
            [addressed.ReadType] = allowed,
        };
        return allowed;
    }

    /// <summary>
    /// The sets the request reads besides the one it addressed: those its navigations reach, and those each search in it
    /// matches through a searchable reference — decided by the binder's own rule
    /// (<see cref="IyuStringSearchBinder.Partition"/>) over the same allowance the binder reads, so the record names
    /// exactly the references the search follows. What a search matches does not depend on its text, only on the type
    /// it binds and the allowance for that type: the addressed type's when the search binds it, none otherwise — the
    /// binder looks the allowance up by type, and only the addressed type is listed.
    /// </summary>
    private static IReadOnlyList<string> PublishedReach(
        IyuEntityPairRegistry registry, string addressedSet, Reach reach, IReadOnlySet<string>? searchAllowed)
    {
        var sets = new HashSet<string>(reach.Sets, StringComparer.Ordinal);
        var addressedType = registry.Find(addressedSet)?.ReadType;
        foreach (var searchedSet in reach.Searched)
        {
            if (registry.Find(searchedSet) is not { } searched) continue;
            var allowed = searched.ReadType == addressedType ? searchAllowed : null;
            foreach (var navigation in IyuStringSearchBinder.Partition(registry, searched.ReadType, allowed).Followed)
                sets.Add(registry.FindByReadType(navigation.PropertyType)!.SetName);
        }
        return sets.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>A query this filter could not account for in full — refused, with words for the caller.</summary>
    private sealed class UninterpretedQueryException(string message) : Exception(message);

    /// <summary>
    /// Parses the request's path-naming options against the EDM and collects the name of every entity set a
    /// navigation in them reaches, at any depth, and of every set a search nested in them binds. A parsed navigation
    /// names its target navigation source directly, so no mapping from property name to set has to be reinvented here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One parser for all of them, built from the route's own services. One parser, because the options read each
    /// other: an <c>$orderby</c> after an <c>$apply</c> sorts by the aliases the apply introduced, and a
    /// <c>$select</c> may name what a <c>$compute</c> defined. The route's services, so that names resolve with the
    /// resolver and settings the query pipeline uses — measured: the route resolves <c>$expand=secret</c> to the
    /// navigation <c>Secret</c> without regard to case, a default parser does not, and a caller holding the target's
    /// policy was refused a request the pipeline accepts. The fail-closed refusals are for disagreements that
    /// remain; this keeps them from firing on ordinary input.
    /// </para>
    /// </remarks>
    private static Reach ReachedSets(
        IEdmModel model, string setName, Dictionary<string, string> options, IServiceProvider? routeServices)
    {
        var entitySet = model.EntityContainer?.FindEntitySet(setName)
                        ?? throw new UninterpretedQueryException($"Entity set '{setName}' is not in the model.");

        var parser = routeServices is null
            ? new ODataQueryOptionParser(model, entitySet.EntityType, entitySet, options)
            : new ODataQueryOptionParser(model, entitySet.EntityType, entitySet, options, routeServices);

        var reach = new Reach();
        Interpret("$apply", () => reach.Walk(parser.ParseApply()));
        Interpret("$compute", () => reach.Walk(parser.ParseCompute()));
        Interpret("$filter", () => reach.Walk(parser.ParseFilter()));
        Interpret("$orderby", () => reach.Walk(parser.ParseOrderBy()));
        Interpret(options.ContainsKey("$expand") ? "$expand" : "$select", () => reach.Walk(parser.ParseSelectAndExpand()));
        // Whatever an alias stands for was bound while the options above were parsed.
        Interpret("parameter alias", () =>
        {
            foreach (var value in parser.ParameterAliasNodes.Values) reach.Visit(value);
        });
        return reach;
    }

    private static void Interpret(string option, Action parse)
    {
        try
        {
            parse();
        }
        catch (ODataException) when (option == "$expand")
        {
            throw new UninterpretedQueryException(
                "The $expand expression could not be interpreted and was refused. Expand each navigation property "
                + "by name, without a nested option this server does not accept.");
        }
        catch (ODataException e)
        {
            throw new UninterpretedQueryException($"The {option} expression could not be interpreted and was refused: {e.Message}");
        }
        catch (NotImplementedException)
        {
            // A node kind the walk below has no rule for. What it might reach is unknown, so it reaches nothing.
            throw new UninterpretedQueryException(
                $"The {option} expression uses a construct this server does not check for access and was refused.");
        }
    }

    /// <summary>
    /// Collects the entity sets the navigations in a parsed query reach. Every kind of node is either walked into or
    /// known to hold no path; a kind with no rule here is not skipped but refused (the base visitor throws).
    /// </summary>
    private sealed class Reach : QueryNodeVisitor<bool>
    {
        public HashSet<string> Sets { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The sets whose type a search binds — the search may match a searchable reference of that type, which the
        /// caller resolves (the walk has no registry). The addressed set's own search is added by the caller.
        /// </summary>
        public HashSet<string> Searched { get; } = new(StringComparer.Ordinal);

        // An unbound navigation has no set; recorded as an empty name, which no set has, so the caller refuses it
        // instead of skipping it.
        private void Reached(IEdmNavigationSource? source) => Sets.Add(source?.Name ?? "");

        public void Visit(QueryNode? node) => node?.Accept(this);

        // ---------- clauses ----------

        public void Walk(FilterClause? clause) => Visit(clause?.Expression);

        public void Walk(OrderByClause? clause)
        {
            for (; clause is not null; clause = clause.ThenBy) Visit(clause.Expression);
        }

        public void Walk(ComputeClause? clause)
        {
            if (clause is null) return;
            foreach (var item in clause.ComputedItems) Visit(item.Expression);
        }

        /// <summary>
        /// A search over the rows of <paramref name="owner"/>. It names no path itself, but it may match a searchable
        /// reference of the owner's type — so the owner is recorded. A search whose rows belong to no set this walk can
        /// name may follow references nothing here can account for, and is refused like any construct without a rule.
        /// </summary>
        private void Walk(SearchClause? clause, IEdmNavigationSource? owner)
        {
            if (clause is null) return;
            if (owner is null) throw new NotImplementedException();
            Searched.Add(owner.Name);
            Visit(clause.Expression);
        }

        public void Walk(SelectExpandClause? clause)
        {
            if (clause is null) return;
            foreach (var item in clause.SelectedItems)
            {
                switch (item)
                {
                    case ExpandedNavigationSelectItem navigation:
                        Reached(navigation.NavigationSource);
                        Walk(navigation.PathToNavigationProperty);
                        Walk(navigation.FilterOption);
                        Walk(navigation.OrderByOption);
                        Walk(navigation.ComputeOption);
                        Walk(navigation.ApplyOption);
                        Walk(navigation.SearchOption, navigation.NavigationSource);
                        Walk(navigation.SelectAndExpand);
                        break;
                    case ExpandedReferenceSelectItem reference:                  // includes $expand=Nav/$count
                        Reached(reference.NavigationSource);
                        Walk(reference.PathToNavigationProperty);
                        Walk(reference.FilterOption);
                        Walk(reference.OrderByOption);
                        Walk(reference.ComputeOption);
                        Walk(reference.ApplyOption);
                        Walk(reference.SearchOption, reference.NavigationSource);
                        break;
                    case PathSelectItem path:
                        Walk(path.SelectedPath);
                        Walk(path.FilterOption);
                        Walk(path.OrderByOption);
                        Walk(path.ComputeOption);
                        // A selected property's rows are not a set's rows — a search there has no owner to name.
                        Walk(path.SearchOption, owner: null);
                        Walk(path.SelectAndExpand);
                        break;
                    case WildcardSelectItem or NamespaceQualifiedWildcardSelectItem:
                        break;
                    default:
                        throw new NotImplementedException();
                }
            }
        }

        /// <summary>The navigations a select or expand path passes through on its way.</summary>
        private void Walk(ODataPath? path)
        {
            if (path is null) return;
            foreach (var segment in path)
                if (segment is NavigationPropertySegment navigation) Reached(navigation.NavigationSource);
        }

        public void Walk(ApplyClause? clause)
        {
            if (clause is null) return;
            foreach (var transformation in clause.Transformations) Walk(transformation);
        }

        private void Walk(TransformationNode? transformation)
        {
            switch (transformation)
            {
                case null:
                    break;
                case AggregateTransformationNode aggregate:
                    foreach (var expression in aggregate.AggregateExpressions) Walk(expression);
                    break;
                case GroupByTransformationNode groupBy:
                    foreach (var property in groupBy.GroupingProperties) Walk(property);
                    Walk(groupBy.ChildTransformations);
                    break;
                case FilterTransformationNode filter:
                    Walk(filter.FilterClause);
                    break;
                case ComputeTransformationNode compute:
                    foreach (var expression in compute.Expressions) Visit(expression.Expression);
                    break;
                case ExpandTransformationNode expand:
                    Walk(expand.ExpandClause);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        private void Walk(AggregateExpressionBase expression)
        {
            switch (expression)
            {
                case AggregateExpression value:
                    // $count aggregates rows, not a value: its expression is a placeholder that names nothing.
                    if (value.Method != AggregationMethod.VirtualPropertyCount) Visit(value.Expression);
                    break;
                case EntitySetAggregateExpression set:
                    Visit(set.Expression);
                    foreach (var child in set.Children) Walk(child);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        private void Walk(GroupByPropertyNode property)
        {
            Visit(property.Expression);
            foreach (var child in property.ChildTransformations) Walk(child);
        }

        // ---------- nodes that reach a set ----------

        public override bool Visit(SingleNavigationNode nodeIn)
        {
            Reached(nodeIn.NavigationSource);
            Visit(nodeIn.Source);
            return true;
        }

        public override bool Visit(CollectionNavigationNode nodeIn)
        {
            Reached(nodeIn.NavigationSource);
            Visit(nodeIn.Source);
            return true;
        }

        // ---------- nodes that only pass through ----------

        public override bool Visit(AllNode nodeIn) { Visit(nodeIn.Source); Visit(nodeIn.Body); return true; }
        public override bool Visit(AnyNode nodeIn) { Visit(nodeIn.Source); Visit(nodeIn.Body); return true; }
        public override bool Visit(BinaryOperatorNode nodeIn) { Visit(nodeIn.Left); Visit(nodeIn.Right); return true; }
        public override bool Visit(UnaryOperatorNode nodeIn) { Visit(nodeIn.Operand); return true; }
        public override bool Visit(ConvertNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(InNode nodeIn) { Visit(nodeIn.Left); Visit(nodeIn.Right); return true; }
        public override bool Visit(CountNode nodeIn)
        {
            Visit(nodeIn.Source);
            Walk(nodeIn.FilterClause);
            Walk(nodeIn.SearchClause, (nodeIn.Source as CollectionNavigationNode)?.NavigationSource);
            return true;
        }

        public override bool Visit(SingleValuePropertyAccessNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(CollectionPropertyAccessNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(SingleValueOpenPropertyAccessNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(CollectionOpenPropertyAccessNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(SingleComplexNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(CollectionComplexNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(SingleResourceCastNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(CollectionResourceCastNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(SingleValueCastNode nodeIn) { Visit(nodeIn.Source); return true; }
        public override bool Visit(AggregatedCollectionPropertyNode nodeIn) { Visit(nodeIn.Source); return true; }

        public override bool Visit(SingleValueFunctionCallNode nodeIn) => Call(nodeIn.Source, nodeIn.Parameters);
        public override bool Visit(SingleResourceFunctionCallNode nodeIn) => Call(nodeIn.Source, nodeIn.Parameters);
        public override bool Visit(CollectionFunctionCallNode nodeIn) => Call(nodeIn.Source, nodeIn.Parameters);
        public override bool Visit(CollectionResourceFunctionCallNode nodeIn) => Call(nodeIn.Source, nodeIn.Parameters);
        public override bool Visit(NamedFunctionParameterNode nodeIn) { Visit(nodeIn.Value); return true; }

        private bool Call(QueryNode? source, IEnumerable<QueryNode> parameters)
        {
            Visit(source);
            foreach (var parameter in parameters) Visit(parameter);
            return true;
        }

        // ---------- leaves ----------

        // A range variable is the row being read ($it) or a lambda's variable, whose collection was reached above.
        public override bool Visit(ResourceRangeVariableReferenceNode nodeIn) => true;
        public override bool Visit(NonResourceRangeVariableReferenceNode nodeIn) => true;
        public override bool Visit(ConstantNode nodeIn) => true;
        public override bool Visit(CollectionConstantNode nodeIn) => true;
        public override bool Visit(SearchTermNode nodeIn) => true;
        // What an alias stands for is walked from the parser's alias table.
        public override bool Visit(ParameterAliasNode nodeIn) => true;
    }
}
