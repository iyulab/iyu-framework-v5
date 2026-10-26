using HotChocolate.Language;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Sizes a GraphQL document the way the executor will have to read it — with every fragment spread expanded — without
/// expanding it. The executor validates and compiles a document before any limit of its own applies, and fragments that
/// spread each other several times over make that work grow exponentially with the document's length: a few dozen lines
/// hold the server for minutes. Counting with each fragment sized once is linear in the document.
/// </summary>
internal static class IyuGraphQLDocumentBudget
{
    /// <summary>The deepest field nesting a document may have (a fragment spread continues the nesting it sits in).</summary>
    internal const int MaxDepth = 64;

    /// <param name="document">The parsed document.</param>
    /// <param name="maxSelections">The most fields it may select once its fragments are expanded.</param>
    /// <returns>
    /// A reason when the document, read in full, selects more than <paramref name="maxSelections"/> fields or nests deeper
    /// than <see cref="MaxDepth"/>; <see langword="null"/> when it is within both. Every operation and every fragment
    /// counts — the executor validates all of them, whichever operation the request runs.
    /// </returns>
    internal static string? Exceeds(DocumentNode document, int maxSelections)
    {
        var fragments = document.Definitions.OfType<FragmentDefinitionNode>()
            .ToDictionary(f => f.Name.Value, StringComparer.Ordinal);
        var sized = new Dictionary<string, (long Size, int Depth)>(StringComparer.Ordinal);
        var sizing = new HashSet<string>(StringComparer.Ordinal);
        var cap = (long)maxSelections + 1;

        (long Size, int Depth) OfSet(SelectionSetNode set)
        {
            long size = 0;
            var depth = 0;
            foreach (var selection in set.Selections)
            {
                long partSize;
                int partDepth;
                switch (selection)
                {
                    case FieldNode field:
                        var below = field.SelectionSet is { } nested ? OfSet(nested) : (Size: 0L, Depth: 0);
                        (partSize, partDepth) = (1 + below.Size, 1 + below.Depth);
                        break;
                    case InlineFragmentNode inline:
                        (partSize, partDepth) = OfSet(inline.SelectionSet);   // a type condition adds no level of its own
                        break;
                    case FragmentSpreadNode spread:
                        var spreadSize = OfFragment(spread.Name.Value);
                        (partSize, partDepth) = (1 + spreadSize.Size, spreadSize.Depth);
                        break;
                    default:
                        continue;
                }
                size = Math.Min(size + partSize, cap);
                depth = Math.Max(depth, partDepth);
                if (size >= cap || depth > MaxDepth) return (cap, depth);
            }
            return (size, depth);
        }

        (long Size, int Depth) OfFragment(string name)
        {
            if (sized.TryGetValue(name, out var known)) return known;
            // A fragment that spreads itself is invalid GraphQL and the executor reports it; sizing it must not recurse.
            if (!fragments.TryGetValue(name, out var fragment) || !sizing.Add(name)) return (0, 0);
            var result = OfSet(fragment.SelectionSet);
            sizing.Remove(name);
            sized[name] = result;
            return result;
        }

        foreach (var definition in document.Definitions)
        {
            var (size, depth) = definition switch
            {
                OperationDefinitionNode operation => OfSet(operation.SelectionSet),
                FragmentDefinitionNode fragment => OfFragment(fragment.Name.Value),
                _ => (0L, 0),
            };
            if (depth > MaxDepth) return $"nests deeper than {MaxDepth} levels";
            if (size > maxSelections) return $"selects more than {maxSelections} fields once its fragments are expanded";
        }
        return null;
    }
}
