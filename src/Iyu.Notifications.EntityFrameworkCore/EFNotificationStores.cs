using Microsoft.EntityFrameworkCore;

namespace Iyu.Notifications.EntityFrameworkCore;

/// <summary>
/// The inbox over the application's own table. Each call saves what it changed, so inside a transaction the host began on the
/// same context (the same scope) an item is committed or rolled back with the change that raised its event.
/// </summary>
/// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
/// <typeparam name="TRow">The entity of the inbox table.</typeparam>
public sealed class EFNotificationInbox<TContext, TRow>(TContext context) : INotificationInbox
    where TContext : DbContext
    where TRow : class, INotificationInboxRow, new()
{
    /// <inheritdoc />
    public async Task AddAsync(IReadOnlyList<NotificationItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return;

        context.Set<TRow>().AddRange(items.Select(item => new TRow
        {
            Id = item.Id,
            RecipientId = item.RecipientId,
            EventKey = item.EventKey,
            Kind = item.Kind,
            Payload = item.Payload,
            CreatedAt = item.CreatedAt,
            ReadAt = item.ReadAt,
            HandledAt = item.HandledAt,
        }));
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationItem>> ListAsync(
        string recipientId, NotificationInboxFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentNullException.ThrowIfNull(filter);

        var query = context.Set<TRow>().AsNoTracking().Where(r => r.RecipientId == recipientId);
        if (filter.UnreadOnly) query = query.Where(r => r.ReadAt == null);
        if (filter.OpenActionRequestsOnly) query = query.Where(r => r.Kind == NotificationKind.ActionRequest && r.HandledAt == null);

        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(Math.Max(filter.Take, 0)).ToListAsync(cancellationToken);
        return rows.Select(ToItem).ToList();
    }

    /// <inheritdoc />
    public async Task<NotificationItem?> FindAsync(string itemId, string recipientId, CancellationToken cancellationToken = default)
    {
        var row = await context.Set<TRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == itemId && r.RecipientId == recipientId, cancellationToken);
        return row is null ? null : ToItem(row);
    }

    /// <inheritdoc />
    public async Task<bool> MarkReadAsync(string itemId, string recipientId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var row = await context.Set<TRow>().FirstOrDefaultAsync(r => r.Id == itemId && r.RecipientId == recipientId, cancellationToken);
        if (row is null) return false;
        if (row.ReadAt is null)
        {
            row.ReadAt = at;
            await context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkHandledAsync(string itemId, string recipientId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var row = await context.Set<TRow>().FirstOrDefaultAsync(r => r.Id == itemId && r.RecipientId == recipientId, cancellationToken);
        if (row is null) return false;
        if (row.HandledAt is null || row.ReadAt is null)
        {
            row.ReadAt ??= at;
            row.HandledAt ??= at;
            await context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A notice goes by its creation time and an action request by the time it was handled; a request with no handling time is open
    /// and matches neither condition, so no cut-off can reach it.
    /// </remarks>
    public Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        context.Set<TRow>()
            .Where(r => (r.Kind == NotificationKind.Notice && r.CreatedAt < cutoff)
                        || (r.Kind == NotificationKind.ActionRequest && r.HandledAt != null && r.HandledAt < cutoff))
            .ExecuteDeleteAsync(cancellationToken);

    private static NotificationItem ToItem(TRow row) =>
        new(row.Id, row.RecipientId, row.EventKey, row.Kind, row.Payload, row.CreatedAt, row.ReadAt, row.HandledAt);
}

/// <summary>The record of events that reached nobody, over the application's own table.</summary>
/// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
/// <typeparam name="TRow">The entity of the table.</typeparam>
public sealed class EFNotificationUnreachedLog<TContext, TRow>(TContext context) : INotificationUnreachedLog
    where TContext : DbContext
    where TRow : class, INotificationUnreachedRow, new()
{
    /// <inheritdoc />
    public async Task AddAsync(IReadOnlyList<UnreachedNotification> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        context.Set<TRow>().AddRange(entries.Select(entry => new TRow
        {
            Id = Guid.CreateVersion7(entry.OccurredAt).ToString("N"),
            EventKey = entry.EventKey,
            Reason = entry.Reason,
            Detail = entry.Detail,
            OccurredAt = entry.OccurredAt,
        }));
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnreachedNotification>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        var rows = await context.Set<TRow>().AsNoTracking().OrderByDescending(r => r.OccurredAt)
            .Take(Math.Max(take, 0)).ToListAsync(cancellationToken);
        return rows.Select(r => new UnreachedNotification(r.EventKey, r.Reason, r.Detail, r.OccurredAt)).ToList();
    }
}

/// <summary>The administrators' target table, read from the application's own table.</summary>
/// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
/// <typeparam name="TRow">The entity of the table.</typeparam>
public sealed class EFNotificationTargetSource<TContext, TRow>(TContext context) : INotificationTargetSource
    where TContext : DbContext
    where TRow : class, INotificationTargetRow, new()
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationTarget>> TargetsAsync(string eventKey, CancellationToken cancellationToken = default)
    {
        var rows = await context.Set<TRow>().AsNoTracking().Where(r => r.EventKey == eventKey).ToListAsync(cancellationToken);
        return rows.Select(r => new NotificationTarget(r.TargetKind, r.TargetValue)).ToList();
    }
}
