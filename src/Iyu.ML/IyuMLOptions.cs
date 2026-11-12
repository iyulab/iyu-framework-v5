using Microsoft.Extensions.Options;

namespace Iyu.ML;

/// <summary>What <c>AddIyuML</c> is configured with: which datasets the host offers, and how read permissions apply to them.</summary>
public sealed class IyuMLOptions
{
    private readonly List<MLDatasetDefinition> _datasets = [];

    /// <summary>The datasets declared so far.</summary>
    public IReadOnlyList<MLDatasetDefinition> Datasets => _datasets;

    /// <summary>Whether the host stated, with <see cref="NoReadPolicies"/>, that it has no read policies to honour.</summary>
    public bool ReadPoliciesNotUsed { get; private set; }

    /// <summary>
    /// Declares a dataset. An adapter that knows where the rows live calls this for each dataset it serves (the Entity
    /// Framework adapter does it from its own declaration); a host that reads its rows some other way calls it directly and
    /// registers an <see cref="IMLDatasetSource"/> that serves the same names.
    /// </summary>
    /// <param name="definition">The dataset.</param>
    /// <exception cref="ArgumentException">A dataset of that name (ignoring case) is already declared.</exception>
    public IyuMLOptions Dataset(MLDatasetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_datasets.Any(d => string.Equals(d.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A dataset named '{definition.Name}' is already declared (names are compared ignoring case).", nameof(definition));
        _datasets.Add(definition);
        return this;
    }

    /// <summary>
    /// States that this host has no read policies, so there is nothing for a dataset to honour. Say it only when it is true:
    /// a host with entity or property read policies registers an <see cref="IMLReadGuard"/> instead, and one that does both is refused.
    /// </summary>
    public IyuMLOptions NoReadPolicies()
    {
        ReadPoliciesNotUsed = true;
        return this;
    }
}

internal sealed class IyuMLOptionsValidator : IValidateOptions<IyuMLOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuMLOptions options)
    {
        if (options.Datasets.Count == 0)
            return ValidateOptionsResult.Fail("AddIyuML needs at least one dataset: declare one with IyuMLOptions.Dataset, or through an adapter such as the Entity Framework one.");
        return ValidateOptionsResult.Success;
    }
}
