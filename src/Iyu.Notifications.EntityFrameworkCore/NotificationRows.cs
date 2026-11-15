namespace Iyu.Notifications.EntityFrameworkCore;

/// <summary>
/// One notification in one recipient's inbox, as the application's own table holds it. The table needs a key on
/// <see cref="Id"/> and an index over (<see cref="RecipientId"/>, <see cref="CreatedAt"/>) for the inbox listing.
/// </summary>
public interface INotificationInboxRow
{
    /// <summary>The item's id; the table's key.</summary>
    string Id { get; set; }

    /// <summary>The user the item is for.</summary>
    string RecipientId { get; set; }

    /// <summary>The declared event that made the item.</summary>
    string EventKey { get; set; }

    /// <summary>Notice or action request.</summary>
    NotificationKind Kind { get; set; }

    /// <summary>What the application attached when it raised the event; stored and returned unchanged.</summary>
    string? Payload { get; set; }

    /// <summary>When the event was raised.</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the recipient first read it; null while unread.</summary>
    DateTimeOffset? ReadAt { get; set; }

    /// <summary>When the recipient handled the action request; null while open, and always null for a notice.</summary>
    DateTimeOffset? HandledAt { get; set; }
}

/// <summary>An event that reached nobody, as the application's own table holds it. The table needs a key on <see cref="Id"/>.</summary>
public interface INotificationUnreachedRow
{
    /// <summary>The entry's id; the table's key.</summary>
    string Id { get; set; }

    /// <summary>The event that was raised.</summary>
    string EventKey { get; set; }

    /// <summary>Why nobody was reached.</summary>
    NotificationUnreachedReason Reason { get; set; }

    /// <summary>For an unknown target kind, the kind nobody handles; otherwise null.</summary>
    string? Detail { get; set; }

    /// <summary>When the event was raised.</summary>
    DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// One line of the administrators' table: an event reaches a target. The application owns the table and the screen that edits
/// it; the routing only reads it. An index over <see cref="EventKey"/> keeps the lookup cheap.
/// </summary>
public interface INotificationTargetRow
{
    /// <summary>The declared event.</summary>
    string EventKey { get; set; }

    /// <summary>The kind of the target: <c>user</c>, <c>role</c>, or a kind only the application understands.</summary>
    string TargetKind { get; set; }

    /// <summary>A user id, a role name, or whatever the application's kind means.</summary>
    string TargetValue { get; set; }
}
