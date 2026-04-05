using Microsoft.EntityFrameworkCore;

namespace Iyu.Data;

/// <summary>
/// Base <see cref="DbContext"/> that all generated consumer DbContexts
/// (e.g. <c>YesungDbContext</c>) derive from. Automatically registers the
/// <see cref="IyuTimestampInterceptor"/> so that every save operation maintains
/// <c>CreatedAt</c>/<c>UpdatedAt</c> invariants.
/// </summary>
/// <remarks>
/// Consumers pass <see cref="DbContextOptions"/> through the standard EF Core
/// DI pipeline. Additional interceptors supplied via <see cref="DbContextOptionsBuilder"/>
/// are preserved — this class only ensures the timestamp interceptor is present.
/// </remarks>
public abstract class IyuDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        // Idempotent registration — harmless if consumer already added one.
        optionsBuilder.AddInterceptors(new IyuTimestampInterceptor());
    }
}
