using Microsoft.Extensions.Options;

namespace Iyu.Ml;

/// <summary>What <c>AddIyuMl</c> is configured with: which datasets the host offers, and how read permissions apply to them.</summary>
public sealed class IyuMlOptions
{
    private readonly List<MlDatasetDefinition> _datasets = [];

    /// <summary>The datasets declared so far.</summary>
    public IReadOnlyList<MlDatasetDefinition> Datasets => _datasets;

    /// <summary>Whether the host stated, with <see cref="NoReadPolicies"/>, that it has no read policies to honour.</summary>
    public bool ReadPoliciesNotUsed { get; private set; }

    /// <summary>
    /// Declares a dataset. An adapter that knows where the rows live calls this for each dataset it serves (the Entity
    /// Framework adapter does it from its own declaration); a host that reads its rows some other way calls it directly and
    /// registers an <see cref="IMlDatasetSource"/> that serves the same names.
    /// </summary>
    /// <param name="definition">The dataset.</param>
    /// <exception cref="ArgumentException">A dataset of that name (ignoring case) is already declared.</exception>
    public IyuMlOptions Dataset(MlDatasetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_datasets.Any(d => string.Equals(d.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A dataset named '{definition.Name}' is already declared (names are compared ignoring case).", nameof(definition));
        _datasets.Add(definition);
        return this;
    }

    /// <summary>
    /// States that this host has no read policies, so there is nothing for a dataset to honour. Say it only when it is true:
    /// a host with entity or property read policies registers an <see cref="IMlReadGuard"/> instead, and one that does both is refused.
    /// </summary>
    public IyuMlOptions NoReadPolicies()
    {
        ReadPoliciesNotUsed = true;
        return this;
    }
}

internal sealed class IyuMlOptionsValidator : IValidateOptions<IyuMlOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuMlOptions options)
    {
        if (options.Datasets.Count == 0)
            return ValidateOptionsResult.Fail("AddIyuMl needs at least one dataset: declare one with IyuMlOptions.Dataset, or through an adapter such as the Entity Framework one.");
        return ValidateOptionsResult.Success;
    }
}
