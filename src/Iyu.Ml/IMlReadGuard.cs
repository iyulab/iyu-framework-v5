using System.Security.Claims;

namespace Iyu.Ml;

/// <summary>
/// How the host's read permissions reach a dataset. Exporting a column is reading it, so a dataset must never hand a caller
/// — or a training engine acting for them — what the host would refuse that caller on its own read surfaces. The host says
/// which columns carry a read policy and whether a given caller may read an entity at all; a source over the host's data
/// consults it.
/// </summary>
/// <remarks>
/// <para>
/// An implementation answers for every type that reaches the same rows, not only the exact type it is asked about: the entity's
/// base and derived types, and any other type the host serves over the same data. A policy that holds on one road to the rows
/// and not on another is no policy.
/// </para>
/// <para>
/// <see cref="IyuMlServiceCollectionExtensions.AddIyuMl"/> will not start a host that has neither registered a guard nor said, with
/// <see cref="IyuMlOptions.NoReadPolicies"/>, that it has no read policies: staying silent would read as "nothing is
/// restricted" in a host where something is.
/// </para>
/// </remarks>
public interface IMlReadGuard
{
    /// <summary>
    /// Why <paramref name="property"/> of <paramref name="entity"/> cannot be a feature or the label of a dataset, or
    /// <c>null</c> when it can. Asked once for each column when the datasets are declared, so a host that declares a
    /// restricted column does not start. The answer names the column and the reason, never a value.
    /// </summary>
    /// <param name="entity">The entity type the dataset reads.</param>
    /// <param name="property">The property name.</param>
    string? RefusalFor(Type entity, string property);

    /// <summary>
    /// Whether <paramref name="caller"/> may read <paramref name="entity"/> at all. Asked on every export; a caller who may
    /// not gets no rows, not fewer columns.
    /// </summary>
    /// <param name="entity">The entity type the dataset reads.</param>
    /// <param name="caller">Who exports.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<bool> MayReadAsync(Type entity, ClaimsPrincipal caller, CancellationToken cancellationToken = default);
}
