using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Reads which served entities a GraphQL operation selects, from its document and the schema alone — nothing runs.
/// </summary>
internal static class IyuGraphQLReach
{
    /// <summary>The most selections one operation may make us visit. The document is read <b>before</b> it is validated, so
    /// its depth and complexity limits have not applied yet: fragments that spread each other several times over would
    /// otherwise expand exponentially. A document past this is not the one a host needs a record of — the executor
    /// refuses or answers it, and no reach is recorded.</summary>
    internal const int MaxSelections = 10_000;

    /// <summary>The deepest selection nesting followed (recursion depth).</summary>
    internal const int MaxDepth = 64;

    /// <param name="schema">The executor's schema — what each selected field's type is.</param>
    /// <param name="document">The request's parsed document.</param>
    /// <param name="operationName">The operation the request names, or <see langword="null"/> for a single-operation document.</param>
    /// <param name="queryNames">The query fields the schema registers — what a root field must be to count as addressed.</param>
    /// <param name="queryNameOf">The query a served read type is exposed under, or <see langword="null"/>.</param>
    /// <returns>
    /// The addressed and reached query names, or <see langword="null"/> when <paramref name="operationName"/> does not
    /// pick a query operation of the document (none, or several with no name given), or when the document is too large to
    /// read in full (<see cref="MaxSelections"/>, <see cref="MaxDepth"/>).
    /// </returns>
    internal static (IReadOnlyList<string> Addressed, IReadOnlyList<string> Reached)? Of(
        ISchemaDefinition schema, DocumentNode document, string? operationName,
        IReadOnlyCollection<string> queryNames, Func<Type, string?> queryNameOf)
    {
        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        var operation = operationName is null
            ? (operations.Count == 1 ? operations[0] : null)
            : operations.FirstOrDefault(o => string.Equals(o.Name?.Value, operationName, StringComparison.Ordinal));
        if (operation is null || operation.Operation != OperationType.Query) return null;

        var queries = new HashSet<string>(queryNames, StringComparer.Ordinal);
        var fragments = document.Definitions.OfType<FragmentDefinitionNode>()
            .ToDictionary(f => f.Name.Value, StringComparer.Ordinal);
        var addressed = new SortedSet<string>(StringComparer.Ordinal);
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        // A fragment is walked once per context (where it was spread from), never again: what a walk adds is the same
        // every time, so a second walk only costs.
        var walked = new HashSet<(string Fragment, bool AtRoot, string? Ignore)>();
        var visited = 0;
        var exhausted = false;

        // `ignore` is the root query whose connection wrapper (nodes, edges.node) is being walked: the first served
        // entity under it is the addressed one itself, not something reached.
        void Walk(SelectionSetNode selections, ITypeDefinition parent, bool atRoot, string? ignore, int depth)
        {
            foreach (var selection in selections.Selections)
            {
                if (exhausted) return;
                if (++visited > MaxSelections || depth > MaxDepth)
                {
                    exhausted = true;
                    return;
                }

                switch (selection)
                {
                    case FieldNode field when !field.Name.Value.StartsWith("__", StringComparison.Ordinal):
                        if (parent is not IComplexTypeDefinition complex
                            || !complex.Fields.TryGetField(field.Name.Value, out var definition))
                            break;
                        var named = definition.Type.NamedType();
                        string? childIgnore = ignore;
                        if (atRoot)
                        {
                            if (!queries.Contains(field.Name.Value)) break;
                            addressed.Add(field.Name.Value);
                            childIgnore = field.Name.Value;
                        }
                        else if (named is IObjectTypeDefinition target && queryNameOf(target.RuntimeType) is { } query)
                        {
                            if (!string.Equals(query, ignore, StringComparison.Ordinal)) reached.Add(query);
                            childIgnore = null;
                        }
                        if (field.SelectionSet is { } nested) Walk(nested, named, atRoot: false, childIgnore, depth + 1);
                        break;

                    case InlineFragmentNode inline:
                        var scope = inline.TypeCondition is { } condition
                            && schema.Types.TryGetType<ITypeDefinition>(condition.Name.Value, out var conditioned)
                                ? conditioned
                                : parent;
                        Walk(inline.SelectionSet, scope, atRoot, ignore, depth);
                        break;

                    case FragmentSpreadNode spread
                        when fragments.TryGetValue(spread.Name.Value, out var fragment)
                             && walked.Add((spread.Name.Value, atRoot, ignore)):
                        if (schema.Types.TryGetType<ITypeDefinition>(fragment.TypeCondition.Name.Value, out var fragmentType))
                            Walk(fragment.SelectionSet, fragmentType, atRoot, ignore, depth + 1);
                        break;
                }
            }
        }

        if (schema.QueryType is { } root) Walk(operation.SelectionSet, root, atRoot: true, ignore: null, depth: 0);
        return exhausted ? null : (addressed.ToList(), reached.ToList());
    }
}
