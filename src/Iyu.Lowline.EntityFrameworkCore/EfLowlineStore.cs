using Iyu.Core.Lowline;
using Lowline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.Lowline.EntityFrameworkCore;

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
/// <para>
/// A record settled more than once keeps one row for each time (the engine's contract since Lowline 0.12): the engine
/// learns a record from its latest row and counts a later check as an audit of the decision first settled, which the
/// earlier row is. The record table therefore may not be unique over the record id — the store checks the model the
/// first time it is used and says so, rather than letting every second save of an id fail on the database's key.
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

    private volatile bool _modelChecked;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LowlineScope>> ListScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        EnsureRecordModel(db);
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
        EnsureRecordModel(db);
        var form = scope.Form;
        var space = scope.Space;
        // One id however many rows (settlements) it has.
        return await db.Set<TRecord>().AsNoTracking()
            .Where(r => r.Form == form && r.Space == space)
            .Select(r => r.RecordId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SettledRecord>> ReadRecordsAsync(LowlineScope scope, CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        EnsureRecordModel(db);
        var form = scope.Form;
        var space = scope.Space;
        var rows = await db.Set<TRecord>().AsNoTracking()
            .Where(r => r.Form == form && r.Space == space)
            .ToListAsync(cancellationToken);
        // Every row, a record settled more than once giving one for each time.
        return rows
            .OrderBy(r => r.SettledAt)
            .ThenBy(r => r.RecordId, StringComparer.Ordinal)
            .Select(r => new SettledRecord(r.RecordId, r.SettledAt, r.Json))
            .ToList();
    }

    /// <inheritdoc />
    public async Task AddRecordAsync(LowlineScope scope, SettledRecord record, CancellationToken cancellationToken = default)
    {
        await using var services = scopes.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<TContext>();
        EnsureRecordModel(db);
        // A row beside the ones kept before, never in place of one: nothing here looks for an earlier row of the id.
        db.Set<TRecord>().Add(new TRecord
        {
            Form = scope.Form,
            Space = scope.Space,
            RecordId = record.Id,
            SettledAt = record.SettledAt,
            Json = record.Json,
        });
        await db.SaveChangesAsync(cancellationToken);
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
        EnsureRecordModel(db);
        var form = scope.Form;
        var space = scope.Space;
        var keys = await db.Set<TRecord>().AsNoTracking()
            .Where(r => r.Form == form && r.Space == space)
            .Select(r => new { r.RecordId, r.SettledAt })
            .ToListAsync(cancellationToken);
        // A record is as old as its latest row: one settled again since the cut-off keeps every row it has.
        var gone = keys
            .GroupBy(k => k.RecordId, StringComparer.Ordinal)
            .Select(g => new { Id = g.Key, Last = g.Max(k => k.SettledAt) })
            .Where(r => r.Last < before)
            .OrderBy(r => r.Last)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => r.Id)
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
        EnsureRecordModel(db);
        var form = scope.Form;
        var space = scope.Space;
        var rows = await db.Set<TRecord>()
            .Where(r => r.Form == form && r.Space == space && r.RecordId == id)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return false;
        // Every row of the record — not the latest alone.
        db.Set<TRecord>().RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Refuses a record table that is unique over the record id — a key, an alternate key or a unique index whose columns
    /// are all among form, space and record id. Such a table lets a record be settled once; the second save of an id (a
    /// reopened record, an audit) would then fail on the database's constraint, in the middle of a person's save. Said
    /// once, at first use, with the change to make, instead.
    /// </summary>
    private void EnsureRecordModel(TContext db)
    {
        if (_modelChecked) return;

        var entity = db.Model.FindEntityType(typeof(TRecord))
            ?? throw new InvalidOperationException(
                $"{typeof(TRecord).Name} is not part of the model of {typeof(TContext).Name}, so the Lowline store has no table to keep records in.");

        static bool OnlyIdentity(IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IProperty> properties) =>
            properties.All(p => p.Name is nameof(ILowlineRecordRow.Form) or nameof(ILowlineRecordRow.Space) or nameof(ILowlineRecordRow.RecordId));

        var tooStrict = entity.GetKeys().Where(k => OnlyIdentity(k.Properties)).Select(k => k.Properties)
            .Concat(entity.GetIndexes().Where(i => i.IsUnique && OnlyIdentity(i.Properties)).Select(i => i.Properties))
            .FirstOrDefault();
        if (tooStrict is not null)
        {
            throw new InvalidOperationException(
                $"The table of {typeof(TRecord).Name} is unique over ({string.Join(", ", tooStrict.Select(p => p.Name))}), which lets a record be settled once. " +
                "Since Lowline 0.12 a record settled again keeps its earlier rows, so the table needs a key of its own (a row id) and an ordinary, non-unique index " +
                $"over ({nameof(ILowlineRecordRow.Form)}, {nameof(ILowlineRecordRow.Space)}, {nameof(ILowlineRecordRow.RecordId)}) instead. Rows already there stay as they are.");
        }

        _modelChecked = true;
    }
}
