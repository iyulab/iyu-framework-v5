using Iyu.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Iyu.Data;

/// <summary>
/// EF Core interceptor that populates <see cref="IyuEntity.CreatedAt"/> on insert
/// and <see cref="IyuEntity.UpdatedAt"/> on insert/update. This is the single
/// source of truth for audit timestamps — application code must not set these
/// fields manually, and doing so will be overwritten at save time.
/// </summary>
/// <remarks>
/// Uses <see cref="DateTimeOffset.UtcNow"/> captured once per save operation so
/// that all entities in the same batch share a consistent timestamp. Tests can
/// replace the clock by providing a custom <see cref="TimeProvider"/>.
/// </remarks>
public sealed class IyuTimestampInterceptor(TimeProvider? timeProvider = null) : SaveChangesInterceptor
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyTimestamps(DbContext? context)
    {
        if (context is null) return;

        var now = _clock.GetUtcNow();
        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IyuEntity entity) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    entity.CreatedAt = now;
                    entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entity.UpdatedAt = now;
                    // Guard CreatedAt against accidental mutation.
                    entry.Property(nameof(IyuEntity.CreatedAt)).IsModified = false;
                    break;
            }
        }
    }
}
