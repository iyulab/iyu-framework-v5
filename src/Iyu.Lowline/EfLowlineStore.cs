using Iyu.Core.Lowline;
using Lowline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Lowline;

/// <summary>
/// The engine's <see cref="ILowlineStore"/> over the application's own two tables (<typeparamref name="TRecord"/>
/// and <typeparamref name="TTuning"/>), reached through its <typeparamref name="TContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engines this store serves are long-lived and choose their thresholds again in the background after a save, so
/// they call the store outside any request. Every call therefore opens its own scope and takes a fresh context from
/// it; nothing is held between calls, which keeps the store free of the context's scoped lifetime and of its
/// thread-safety limits.
/// </para>
/// <para>
/// Ordering and the retention cut-off are done on the loaded rows rather than in SQL: a <see cref="DateTimeOffset"/>
/// column is not orderable or comparable on every relational provider (SQLite is one), and a scope's records are read
/// whole to start its engine anyway.
/// </para>
/// </remarks>
public sealed class EfLowlineStore<TContext, TRecord, TTuning>(IServiceScopeFactory scopes) : ILowlineStore, ILowlineRecordIndex
    where TContext : DbContext
    where TRecord : class, ILowlineRecordRow, new()
    where TTuning : class, ILowlineTuningRow, new()
{
    // A key list longer than this is removed in pieces, which keeps a retention purge inside the parameter limit of
    // the providers that have one.
    private const int KeyBatch = 500;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LowlineScope>> ListScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        var pairs = await db.Set<TRecord>().AsNoTracking()
            .Select(r => new { r.Form, r.Space })
            .Distinct()
            .ToListAsync(cancellationToken);
        return pairs.Select(p => new LowlineScope(p.Form, p.Space)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListRecordIdsAsync(LowlineScope scope, CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        var form = scope.Form;
        var space = scope.Space;
        return await db.Set<TRecord>().AsNoTracking()
            .Where(r => r.Form == form && r.Space == space)
            .Select(r => r.RecordId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SettledRecord>> ReadRecordsAsync(LowlineScope scope, CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        var form = scope.Form;
        var space = scope.Space;
        var rows = await db.Set<TRecord>().AsNoTracking()
            .Where(r => r.Form == form && r.Space == space)
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(r => r.SettledAt)
            .ThenBy(r => r.RecordId, StringComparer.Ordinal)
            .Select(r => new SettledRecord(r.RecordId, r.SettledAt, r.Json))
            .ToList();
    }

    /// <inheritdoc />
    public async Task AddRecordAsync(LowlineScope scope, SettledRecord record, CancellationToken cancellationToken = default)
    {
        // Two saves of one id racing for the first insert: the loser meets the unique key, and on its second turn finds
        // the winner's row and replaces it — which is what "replacing the scope's record of the same id" asks.
        for (var attempt = 1; ; attempt++)
        {
            await using var services = scopes.CreateAsyncScope();
            var db = services.ServiceProvider.GetRequiredService<TContext>();
            var form = scope.Form;
            var space = scope.Space;
            var id = record.Id;
            var row = await db.Set<TRecord>()
                .Where(r => r.Form == form && r.Space == space && r.RecordId == id)
                .SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                db.Set<TRecord>().Add(new TRecord
                {
                    Form = form,
                    Space = space,
                    RecordId = id,
                    SettledAt = record.SettledAt,
                    Json = record.Json,
                });
            }
            else
            {
                row.SettledAt = record.SettledAt;
                row.Json = record.Json;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (row is null && attempt == 1)
            {
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> ReadTuningAsync(LowlineScope scope, CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        var form = scope.Form;
        var space = scope.Space;
        var row = await db.Set<TTuning>().AsNoTracking()
            .Where(t => t.Form == form && t.Space == space)
            .SingleOrDefaultAsync(cancellationToken);
        return row?.Tuning;
    }

    /// <inheritdoc />
    public async Task WriteTuningAsync(LowlineScope scope, string tuning, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var services = scopes.CreateAsyncScope();
            var db = services.ServiceProvider.GetRequiredService<TContext>();
            var form = scope.Form;
            var space = scope.Space;
            var row = await db.Set<TTuning>()
                .Where(t => t.Form == form && t.Space == space)
                .SingleOrDefaultAsync(cancellationToken);
            if (row is null)
                db.Set<TTuning>().Add(new TTuning { Form = form, Space = space, Tuning = tuning });
            else
                row.Tuning = tuning;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (row is null && attempt == 1)
            {
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ForgetBeforeAsync(LowlineScope scope, DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        var form = scope.Form;
        var space = scope.Space;
        var keys = await db.Set<TRecord>().AsNoTracking()
            .Where(r => r.Form == form && r.Space == space)
            .Select(r => new { r.RecordId, r.SettledAt })
            .ToListAsync(cancellationToken);
        var gone = keys.Where(k => k.SettledAt < before)
            .OrderBy(k => k.SettledAt)
            .ThenBy(k => k.RecordId, StringComparer.Ordinal)
            .Select(k => k.RecordId)
            .ToList();
        foreach (var batch in gone.Chunk(KeyBatch))
        {
            var ids = batch;
            var rows = await db.Set<TRecord>()
                .Where(r => r.Form == form && r.Space == space && ids.Contains(r.RecordId))
                .ToListAsync(cancellationToken);
            db.Set<TRecord>().RemoveRange(rows);
        }

        await db.SaveChangesAsync(cancellationToken);
        return gone;
    }

    /// <inheritdoc />
    public async Task<bool> ForgetAsync(LowlineScope scope, string id, CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        var form = scope.Form;
        var space = scope.Space;
        var row = await db.Set<TRecord>()
            .Where(r => r.Form == form && r.Space == space && r.RecordId == id)
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return false;
        db.Set<TRecord>().Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
