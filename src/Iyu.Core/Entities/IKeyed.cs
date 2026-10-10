namespace Iyu.Core.Entities;

/// <summary>
/// A row the runtime can serve by key: a stable <see cref="Guid"/> named <c>Id</c>, and nothing else.
/// </summary>
/// <remarks>
/// <see cref="IyuEntity"/> is this plus the audit timestamps the runtime maintains on what it writes. A model that is
/// only ever read — a view joining or aggregating other tables — has a key to be addressed by and no timestamps of
/// its own, so it implements this instead of inheriting columns it does not have.
/// </remarks>
public interface IKeyed
{
    /// <summary>The stable key the row is addressed by.</summary>
    Guid Id { get; }
}
