namespace Iyu.Core.Entities;

/// <summary>
/// Base class for all Iyu-managed entities. Provides the common identity and
/// audit timestamp contract consumed by <c>IyuDbContext</c> and the OData/GraphQL
/// runtime. Generated entities inherit from this class; extension via
/// <c>partial class</c> is supported.
/// </summary>
/// <remarks>
/// Both <see cref="CreatedAt"/> and <see cref="UpdatedAt"/> are populated by the
/// <c>IyuDbContext</c> <c>SaveChanges</c> interceptor. Consumers should not set
/// these fields manually; the interceptor is the single source of truth.
/// </remarks>
public abstract class IyuEntity
{
    /// <summary>The stable primary key. Generated entities default to <see cref="Guid"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>UTC timestamp of first insertion. Assigned by the DbContext interceptor.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of last modification. Refreshed by the DbContext interceptor.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
