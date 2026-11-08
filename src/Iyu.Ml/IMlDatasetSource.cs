using System.Security.Claims;

namespace Iyu.Ml;

/// <summary>
/// The port a training set is read through. An implementation turns a declared dataset into rows from wherever the host keeps
/// them; the layer that uses the rows — an exporter, a baseline fit, an engine adapter — never learns where that is.
/// </summary>
/// <remarks>
/// The caller is part of the question. Exporting rows is reading them, so the identity that asks is the identity whose read
/// permissions apply; an implementation must not return what that caller could not read directly, and must not serve a
/// dataset to an anonymous caller. The parameter is required for that reason and has no default. A source over the host's own
/// data consults the registered <see cref="IMlReadGuard"/>: it refuses a column the guard refuses when the datasets are
/// declared, and returns no rows to a caller the guard says may not read the entity.
/// </remarks>
public interface IMlDatasetSource
{
    /// <summary>The datasets this source can read.</summary>
    IReadOnlyList<MlDatasetDefinition> Datasets { get; }

    /// <summary>
    /// Reads the rows of <paramref name="dataset"/>. Each row is keyed by the definition's column names; a column a row has no
    /// value for is absent or <c>null</c>.
    /// </summary>
    /// <param name="dataset">The dataset's name, as in <see cref="Datasets"/>.</param>
    /// <param name="caller">Who is asking; their read permissions bound what is returned.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentException">No dataset has that name.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not read the dataset.</exception>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadAsync(
        string dataset, ClaimsPrincipal caller, CancellationToken cancellationToken = default);
}
