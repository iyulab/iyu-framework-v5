using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;

namespace Iyu.MainServer.Identity;

/// <summary>
/// An <see cref="ISessionEndStore"/> over the application's <see cref="IDistributedCache"/>: one entry per
/// ended session, which the cache drops by itself once it is no longer needed.
/// </summary>
/// <remarks>
/// <para>
/// Register it next to a distributed cache that every instance shares (Redis, SQL Server, …):
/// <c>services.AddSingleton&lt;ISessionEndStore, DistributedCacheSessionEndStore&gt;()</c>.
/// </para>
/// <para>
/// Over <c>AddDistributedMemoryCache()</c> the record lives in one process: right for a single instance and for
/// tests, and wrong behind a load balancer — the session would end only where the notice arrived.
/// </para>
/// </remarks>
public sealed class DistributedCacheSessionEndStore : ISessionEndStore
{
    private static readonly byte[] Ended = [1];

    private readonly IDistributedCache _cache;

    public DistributedCacheSessionEndStore(IDistributedCache cache) => _cache = cache;

    /// <inheritdoc />
    public Task RecordEndedAsync(SessionKey session, DateTimeOffset keepUntil, CancellationToken ct)
        => _cache.SetAsync(Key(session), Ended, new DistributedCacheEntryOptions { AbsoluteExpiration = keepUntil }, ct);

    /// <inheritdoc />
    public async Task<bool> HasEndedAsync(SessionKey session, CancellationToken ct)
        => await _cache.GetAsync(Key(session), ct) is not null;

    /// <summary>
    /// The entry's key. The provider's length is written before it — and a session of this application's own, which
    /// has no provider, has a key of its own shape — so no session can produce another's key whatever characters
    /// either holds.
    /// </summary>
    internal static string Key(SessionKey session)
        => session.Provider is { } provider
            ? string.Create(CultureInfo.InvariantCulture, $"iyu:session-ended:{provider.Length}:{provider}:{session.Id}")
            : $"iyu:session-ended:app:{session.Id}";
}
