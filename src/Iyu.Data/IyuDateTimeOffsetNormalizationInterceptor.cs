using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Iyu.Data;

/// <summary>
/// EF Core interceptor that normalizes every <see cref="DateTimeOffset"/>-typed
/// property on added/modified entities to UTC (<c>Offset == TimeSpan.Zero</c>)
/// immediately before it reaches the database provider.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL's <c>timestamp with time zone</c> — and Npgsql (v6+), which
/// enforces the column's own constraint — accept only <c>Offset == TimeSpan.Zero</c>;
/// any other offset throws at <c>SaveChangesAsync</c>. A <see cref="DateTimeOffset"/>
/// bound from client input without an explicit UTC offset (OData model binding,
/// <see cref="DateTimeOffset.Parse(string)"/>) commonly ends up carrying the
/// server process's local offset instead of zero, so a request that looks
/// well-formed can still fail to save — and, unhandled, surfaces as a 500 rather
/// than the write error it actually is.
/// </para>
/// <para>
/// <see cref="DateTimeOffset.ToUniversalTime"/> re-expresses the same represented
/// instant at offset zero — it does not shift the point in time — so this is
/// pure normalization, not reinterpretation of intent. Running it here, once,
/// makes every save path (OData, direct EF usage, seeding) behave identically
/// regardless of which provider is configured, the same way
/// <see cref="IyuTimestampInterceptor"/> centralizes the audit-timestamp
/// invariant rather than leaving each caller to remember it.
/// </para>
/// </remarks>
public sealed class IyuDateTimeOffsetNormalizationInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Normalize(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Normalize(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Normalize(DbContext? context)
    {
        if (context is null) return;

        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            foreach (PropertyEntry property in entry.Properties)
            {
                if (property.CurrentValue is DateTimeOffset dto && dto.Offset != TimeSpan.Zero)
                    property.CurrentValue = dto.ToUniversalTime();
            }
        }
    }
}
