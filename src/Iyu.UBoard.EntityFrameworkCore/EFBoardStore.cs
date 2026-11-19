using Microsoft.EntityFrameworkCore;

namespace Iyu.UBoard.EntityFrameworkCore;

/// <summary>
/// The board store over the application's own tables. A save is decided by the database, not by a read followed by a write: the
/// board's row is moved from the revision the caller named to the next one in a single conditional update, so of two saves on one
/// revision exactly one succeeds. The revision row and the board row are written in one transaction — the host's, when it has begun
/// one on the same context, otherwise one this store begins (through the context's execution strategy, so a retrying strategy works).
/// </summary>
/// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
/// <typeparam name="TBoard">The entity of the board table.</typeparam>
/// <typeparam name="TRevision">The entity of the revision table.</typeparam>
/// <typeparam name="TPublication">The entity of the publication history table.</typeparam>
public sealed class EFBoardStore<TContext, TBoard, TRevision, TPublication>(TContext context) : IBoardStore
    where TContext : DbContext
    where TBoard : class, IBoardRow, new()
    where TRevision : class, IBoardRevisionRow, new()
    where TPublication : class, IBoardPublicationRow, new()
{
    private const int PublishAttempts = 3;

    /// <inheritdoc />
    public async Task<BoardChange> CreateAsync(
        string id, string name, string document, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        try
        {
            return await InTransactionAsync(async () =>
            {
                var existing = await Boards().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
                if (existing is not null) return new BoardChange(BoardChangeStatus.AlreadyExists, ToRecord(existing));

                var board = new TBoard { Id = id, Name = name, Document = document, CurrentRevision = 1, PublishedRevision = null };
                var revision = NewRevision(id, 1, document, at, createdBy);
                context.Set<TBoard>().Add(board);
                context.Set<TRevision>().Add(revision);
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                finally
                {
                    context.Entry(board).State = EntityState.Detached;
                    context.Entry(revision).State = EntityState.Detached;
                }

                return new BoardChange(BoardChangeStatus.Done, ToRecord(board));
            }, change => change.Succeeded, cancellationToken);
        }
        catch (DbUpdateException) when (context.Database.CurrentTransaction is null)
        {
            // Two creations of one id raced and this one lost on the key. Its transaction is gone, so the winner can be read.
            var winner = await Boards().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
            if (winner is null) throw;
            return new BoardChange(BoardChangeStatus.AlreadyExists, ToRecord(winner));
        }
    }

    /// <inheritdoc />
    public async Task<BoardRecord?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var row = await Boards().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public Task<BoardChange> SaveAsync(
        string id, string document, int expectedRevision, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async () =>
        {
            var next = expectedRevision + 1;
            var moved = await context.Set<TBoard>()
                .Where(b => b.Id == id && b.CurrentRevision == expectedRevision)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Document, document).SetProperty(b => b.CurrentRevision, next), cancellationToken);

            if (moved == 0)
            {
                var current = await Boards().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
                return current is null
                    ? new BoardChange(BoardChangeStatus.NotFound, null)
                    : new BoardChange(BoardChangeStatus.Conflict, ToRecord(current));
            }

            var revision = NewRevision(id, next, document, at, createdBy);
            context.Set<TRevision>().Add(revision);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                context.Entry(revision).State = EntityState.Detached;
            }

            var saved = await Boards().FirstAsync(b => b.Id == id, cancellationToken);
            return new BoardChange(BoardChangeStatus.Done, ToRecord(saved));
        }, change => change.Succeeded, cancellationToken);

    /// <inheritdoc />
    public async Task<BoardRevisionRecord?> FindRevisionAsync(string id, int revision, CancellationToken cancellationToken = default)
    {
        var row = await context.Set<TRevision>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.BoardId == id && r.Revision == revision, cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardRevisionRecord>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default)
    {
        var rows = await context.Set<TRevision>().AsNoTracking()
            .Where(r => r.BoardId == id).OrderByDescending(r => r.Revision).ToListAsync(cancellationToken);
        return rows.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    public async Task<BoardPublicationRecord?> PublishAsync(
        string id, int? revision, string publishedBy, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        // The history's key makes two publications that chose one place in it unable to both succeed; the one that lost tries again
        // from the history as it now is. Inside a transaction of the host's a lost race cannot be retried (the transaction is spoilt),
        // so the failure is the host's to see.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await InTransactionAsync(async () =>
                {
                    if (!await Boards().AnyAsync(b => b.Id == id, cancellationToken)) return null;
                    if (revision is { } wanted
                        && !await context.Set<TRevision>().AsNoTracking().AnyAsync(r => r.BoardId == id && r.Revision == wanted, cancellationToken))
                        return null;

                    // The board's pointer moves first. That takes the board's row lock, so a delete that is under way finishes before
                    // this goes on (and the board is then gone: no line is written for it), and two publications of one board
                    // are taken in turn. Nothing has been changed when the board is not there.
                    var moved = await context.Set<TBoard>().Where(b => b.Id == id)
                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.PublishedRevision, revision), cancellationToken);
                    if (moved == 0) return null;

                    var last = await context.Set<TPublication>().AsNoTracking()
                        .Where(p => p.BoardId == id).MaxAsync(p => (int?)p.Sequence, cancellationToken);
                    var line = new TPublication
                    {
                        BoardId = id,
                        Sequence = (last ?? 0) + 1,
                        Revision = revision,
                        PublishedAt = at,
                        PublishedBy = publishedBy,
                    };
                    context.Set<TPublication>().Add(line);
                    try
                    {
                        await context.SaveChangesAsync(cancellationToken);
                    }
                    finally
                    {
                        context.Entry(line).State = EntityState.Detached;
                    }

                    return ToRecord(line);
                }, line => line is not null, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < PublishAttempts && context.Database.CurrentTransaction is null)
            {
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardPublicationRecord>> ListPublicationsAsync(string id, CancellationToken cancellationToken = default)
    {
        var rows = await context.Set<TPublication>().AsNoTracking()
            .Where(p => p.BoardId == id).OrderByDescending(p => p.Sequence).ToListAsync(cancellationToken);
        return rows.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken = default) =>
        await Boards().OrderBy(b => b.Id).Skip(skip).Take(take)
            .Select(b => new BoardSummary(b.Id, b.Name, b.CurrentRevision, b.PublishedRevision))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<BoardChange> RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        var renamed = await context.Set<TBoard>().Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Name, name), cancellationToken);
        if (renamed == 0) return new BoardChange(BoardChangeStatus.NotFound, null);

        var board = await Boards().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        return board is null ? new BoardChange(BoardChangeStatus.NotFound, null) : new BoardChange(BoardChangeStatus.Done, ToRecord(board));
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async () =>
        {
            // The board's row is taken first, by an update that changes nothing: the row's lock makes a save or a publication that is
            // under way on the same board finish before this goes on, and one that starts after it find no board. The children are
            // deleted after that, and so include anything such a save or publication wrote — none is left behind with no board — and
            // the board last, which is the order a foreign key from the children to the board needs. All of it is one transaction,
            // so a failure leaves the board whole.
            if (await context.Set<TBoard>().Where(b => b.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Name, b => b.Name), cancellationToken) == 0)
                return false;
            await context.Set<TPublication>().Where(p => p.BoardId == id).ExecuteDeleteAsync(cancellationToken);
            await context.Set<TRevision>().Where(r => r.BoardId == id).ExecuteDeleteAsync(cancellationToken);
            await context.Set<TBoard>().Where(b => b.Id == id).ExecuteDeleteAsync(cancellationToken);
            return true;
        }, deleted => deleted, cancellationToken);

    /// <summary>Boards are read without tracking: a conditional update does not refresh an entity the context is holding.</summary>
    private IQueryable<TBoard> Boards() => context.Set<TBoard>().AsNoTracking();

    /// <summary>
    /// Runs <paramref name="work"/> inside the host's transaction when there is one, and otherwise inside one of its own, committed
    /// only when <paramref name="commit"/> says the work changed something.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, Func<T, bool> commit, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null) return await work();

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var result = await work();
            if (commit(result)) await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private static TRevision NewRevision(string boardId, int revision, string document, DateTimeOffset at, string createdBy) => new()
    {
        BoardId = boardId,
        Revision = revision,
        Document = document,
        CreatedAt = at,
        CreatedBy = createdBy,
    };

    private static BoardRecord ToRecord(TBoard row) =>
        new(row.Id, row.Name, row.Document, row.CurrentRevision, row.PublishedRevision);

    private static BoardRevisionRecord ToRecord(TRevision row) =>
        new(row.BoardId, row.Revision, row.Document, row.CreatedAt, row.CreatedBy);

    private static BoardPublicationRecord ToRecord(TPublication row) =>
        new(row.BoardId, row.Sequence, row.Revision, row.PublishedAt, row.PublishedBy);
}
