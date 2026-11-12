namespace Iyu.ML;

/// <summary>What a dataset column is for.</summary>
public enum MLColumnRole
{
    /// <summary>An input the model reads.</summary>
    Feature,

    /// <summary>The true answer the model is trained to give. At most one per dataset; a dataset without one serves tasks that have no label.</summary>
    Label,
}

/// <summary>One column of a dataset: its name and what it is for.</summary>
/// <param name="Name">The column name; it is the header of an exported file and the key of a row.</param>
/// <param name="Role">What the column is for.</param>
public sealed record MLColumn(string Name, MLColumnRole Role);

/// <summary>
/// A named table a model is trained from: which columns are inputs and which one is the answer. The definition says nothing about
/// where the rows come from — that is an <see cref="IMLDatasetSource"/> — and nothing about which engine reads them.
/// </summary>
public sealed class MLDatasetDefinition
{
    /// <summary>Creates a definition, refusing one a file or a row could not carry unambiguously.</summary>
    /// <param name="name">The dataset's name.</param>
    /// <param name="columns">The columns, in the order an exported file lists them.</param>
    /// <exception cref="ArgumentException">The name is empty, a column name is empty or repeated (ignoring case), there is no feature, or there is more than one label.</exception>
    public MLDatasetDefinition(string name, IEnumerable<MLColumn> columns)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A dataset needs a name.", nameof(name));
        ArgumentNullException.ThrowIfNull(columns);

        var list = columns.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in list)
        {
            if (column is null || string.IsNullOrWhiteSpace(column.Name))
                throw new ArgumentException($"Dataset '{name}' has a column with no name.", nameof(columns));
            if (!seen.Add(column.Name))
                throw new ArgumentException($"Dataset '{name}' names the column '{column.Name}' more than once (names are compared ignoring case).", nameof(columns));
        }

        if (!list.Any(c => c.Role == MLColumnRole.Feature))
            throw new ArgumentException($"Dataset '{name}' has no feature column.", nameof(columns));
        if (list.Count(c => c.Role == MLColumnRole.Label) > 1)
            throw new ArgumentException($"Dataset '{name}' has more than one label column.", nameof(columns));

        Name = name;
        Columns = list;
    }

    /// <summary>The dataset's name.</summary>
    public string Name { get; }

    /// <summary>The columns, in the order an exported file lists them.</summary>
    public IReadOnlyList<MLColumn> Columns { get; }

    /// <summary>The input columns, in column order.</summary>
    public IEnumerable<string> Features => Columns.Where(c => c.Role == MLColumnRole.Feature).Select(c => c.Name);

    /// <summary>The label column, or <c>null</c> when the dataset has none.</summary>
    public string? Label => Columns.FirstOrDefault(c => c.Role == MLColumnRole.Label)?.Name;
}
