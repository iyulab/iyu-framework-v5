using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;

namespace Iyu.MainServer.Identity;

/// <summary>
/// An <see cref="IExternalSessionEndStore"/> over the application's <see cref="IDistributedCache"/>: one entry per
/// ended session, which the cache drops by itself once it is no longer needed.
/// </summary>
/// <remarks>
/// <para>
/// Register it next to a distributed cache that every instance shares (Redis, SQL Server, …):
/// <c>services.AddSingleton&lt;IExternalSessionEndStore, DistributedCacheExternalSessionEndStore&gt;()</c>.
/// </para>
/// <para>
/// Over <c>AddDistributedMemoryCache()</c> the record lives in one process: right for a single instance and for
/// tests, and wrong behind a load balancer — the session would end only where the notice arrived.
/// </para>
/// </remarks>
public sealed class DistributedCacheExternalSessionEndStore : IExternalSessionEndStore
{
    private static readonly byte[] Ended = [1];

    private readonly IDistributedCache _cache;

    public DistributedCacheExternalSessionEndStore(IDistributedCache cache) => _cache = cache;

    /// <inheritdoc />
    public Task RecordEndedAsync(ExternalSession session, DateTimeOffset keepUntil, CancellationToken ct)
        => _cache.SetAsync(Key(session), Ended, new DistributedCacheEntryOptions { AbsoluteExpiration = keepUntil }, ct);

    /// <inheritdoc />
    public async Task<bool> HasEndedAsync(ExternalSession session, CancellationToken ct)
        => await _cache.GetAsync(Key(session), ct) is not null;

    /// <summary>
    /// The entry's key. The provider's length is written before it, so no pair of (provider, session id) can produce
    /// another pair's key whatever characters either holds.
    /// </summary>
    internal static string Key(ExternalSession session)
        => string.Create(CultureInfo.InvariantCulture,
            $"iyu:external-session-ended:{session.Provider.Length}:{session.Provider}:{session.SessionId}");
}
