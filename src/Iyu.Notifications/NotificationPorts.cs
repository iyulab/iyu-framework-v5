namespace Iyu.Notifications;

/// <summary>
/// The administrators' table: which targets an event reaches. The application owns the table and its screen; this port is
/// how the routing reads it.
/// </summary>
public interface INotificationTargetSource
{
    /// <summary>The targets of <paramref name="eventKey"/>; empty when the table names none.</summary>
    /// <param name="eventKey">A declared event.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<NotificationTarget>> TargetsAsync(string eventKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns one target into the users it stands for. The identity kinds (<see cref="NotificationTargetKinds"/>) are handled by
/// this package; an application registers its own for the kinds only it understands.
/// </summary>
/// <remarks>
/// A resolver the application registers always answers before the package's own identity resolver, whatever order the
/// registrations were made in, so an application can replace how users and roles are resolved. When more than one application
/// resolver handles a kind, the one registered last answers.
/// </remarks>
public interface INotificationTargetResolver
{
    /// <summary>Whether this resolver understands targets of <paramref name="targetKind"/>.</summary>
    /// <param name="targetKind">The kind named by a target.</param>
    bool Handles(string targetKind);

    /// <summary>The ids of the active users <paramref name="target"/> stands for; empty when there are none.</summary>
    /// <param name="target">A target of a kind this resolver handles.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IReadOnlyCollection<string>> ResolveAsync(NotificationTarget target, CancellationToken cancellationToken = default);
}

/// <summary>The identity face: who the users and roles are. The host answers from its own accounts.</summary>
public interface INotificationIdentityDirectory
{
    /// <summary>Whether <paramref name="userId"/> is an active user.</summary>
    /// <param name="userId">A user id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<bool> IsActiveUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>The ids of the active users in <paramref name="role"/>.</summary>
    /// <param name="role">A role name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IReadOnlyCollection<string>> ActiveUserIdsInRoleAsync(string role, CancellationToken cancellationToken = default);
}

/// <summary>Where notifications wait for the people they are for.</summary>
/// <remarks>
/// An implementation keeps one invariant above all: <see cref="PurgeAsync"/> never removes an action request that has not
/// been handled. The services in this package rely on it and do not re-check it.
/// </remarks>
public interface INotificationInbox
{
    /// <summary>Puts the items in their recipients' inboxes.</summary>
    /// <param name="items">The items.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task AddAsync(IReadOnlyList<NotificationItem> items, CancellationToken cancellationToken = default);

    /// <summary>The recipient's items, newest first.</summary>
    /// <param name="recipientId">The user.</param>
    /// <param name="filter">Which items.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<NotificationItem>> ListAsync(
        string recipientId, NotificationInboxFilter filter, CancellationToken cancellationToken = default);

    /// <summary>One of the recipient's items, or null when the recipient has no such item.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="recipientId">The user; another user's item is not found.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<NotificationItem?> FindAsync(string itemId, string recipientId, CancellationToken cancellationToken = default);

    /// <summary>Records that the recipient read the item, keeping the first time if it was read before. Returns false when there is no such item.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="recipientId">The user.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> MarkReadAsync(string itemId, string recipientId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the recipient handled the action request (and read it, if it was not read), keeping the first time if it was
    /// handled before. Returns false when there is no such item. The caller has already checked the item is an action request.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="recipientId">The user.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> MarkHandledAsync(string itemId, string recipientId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes what is older than <paramref name="cutoff"/>: notices created before it, and action requests handled before it.
    /// An action request that is still open is never removed, however old. Returns how many items were removed.
    /// </summary>
    /// <param name="cutoff">The moment before which items may go.</param>
    /// <param name="cancellationToken">Cancels the clean-up.</param>
    Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

/// <summary>Where events that reached nobody are kept, so that a missing recipient shows up somewhere a person looks.</summary>
public interface INotificationUnreachedLog
{
    /// <summary>Records the unreached events.</summary>
    /// <param name="entries">What could not be delivered.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task AddAsync(IReadOnlyList<UnreachedNotification> entries, CancellationToken cancellationToken = default);

    /// <summary>The most recent entries, newest first.</summary>
    /// <param name="take">At most this many.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<UnreachedNotification>> ListAsync(int take, CancellationToken cancellationToken = default);
}

/// <summary>
/// A way a notification leaves the inbox — mail, a push, a message. It is an extension point this package only calls: an
/// implementation is its own package, takes its own dependencies, and answers for what it sends outside the organization.
/// </summary>
public interface INotificationDelivery
{
    /// <summary>Delivers an item after it was put in the recipient's inbox. A failure is logged and never undoes the inbox write.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    Task DeliverAsync(NotificationItem item, CancellationToken cancellationToken = default);
}
