using Iyu.Core.Authorization;

namespace Iyu.MainServer;

/// <summary>
/// Merges every registered <see cref="IAuthorizationSurfaceProvider"/>. Registered by
/// <c>AddIyuMainServer</c>; a consumer resolves <see cref="IAuthorizationSurfaceReport"/>.
/// </summary>
/// <remarks>
/// Providers are enumerated from DI rather than named here, so a surface package that registers
/// its own provider is picked up without this type changing.
/// </remarks>
internal sealed class AuthorizationSurfaceReport(IEnumerable<IAuthorizationSurfaceProvider> providers)
    : IAuthorizationSurfaceReport
{
    private readonly Lazy<IReadOnlyList<AuthorizationSurfaceEntry>> _entries = new(() =>
        providers
            .SelectMany(p => p.Describe())
            .OrderBy(e => e.Surface, StringComparer.Ordinal)
            .ThenBy(e => e.Entity, StringComparer.Ordinal)
            .ThenBy(e => e.Operation)
            .ToList());

    /// <inheritdoc />
    public IReadOnlyList<AuthorizationSurfaceEntry> Entries => _entries.Value;

    /// <inheritdoc />
    public IReadOnlyList<AuthorizationSurfaceEntry> Unprotected =>
        [.. Entries.Where(e => e.Policy is null)];
}
