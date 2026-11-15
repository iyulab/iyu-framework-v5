namespace Iyu.Notifications;

/// <summary>What a notification asks of the person who receives it.</summary>
public enum NotificationKind
{
    /// <summary>Something to know. It is read, and that is all: a notice has no "handled" state.</summary>
    Notice = 0,

    /// <summary>Something to do. It stays open until the recipient marks it handled, and no clean-up removes it before then.</summary>
    ActionRequest = 1,
}

/// <summary>An event the application declared: what it is called and what kind of notification it makes.</summary>
/// <param name="Key">The identifier code raises it by, for example <c>work-order.delayed</c>. Compared ignoring case.</param>
/// <param name="Name">What a person reading an inbox or an administration screen sees; the key when none was given.</param>
/// <param name="Kind">Whether it informs or asks for action.</param>
/// <param name="Description">A sentence about when it happens, for administration screens; may be empty.</param>
public sealed record NotificationEventDefinition(string Key, string Name, NotificationKind Kind, string? Description);

/// <summary>One entry of the table an administrator edits: an event is to reach this target.</summary>
/// <param name="Kind">
/// How <paramref name="Value"/> is read. <see cref="NotificationTargetKinds.User"/> and <see cref="NotificationTargetKinds.Role"/>
/// are the identity face this package resolves itself; any other kind (a department, a process, a device) means something only
/// to the application, so the application registers an <see cref="INotificationTargetResolver"/> for it.
/// </param>
/// <param name="Value">A user id, a role name, or whatever the application's kind means.</param>
public sealed record NotificationTarget(string Kind, string Value);

/// <summary>The target kinds this package resolves on its own.</summary>
public static class NotificationTargetKinds
{
    /// <summary>The value is a user id.</summary>
    public const string User = "user";

    /// <summary>The value is a role name; every active user in the role is a recipient.</summary>
    public const string Role = "role";
}

/// <summary>A notification in one recipient's inbox.</summary>
/// <param name="Id">The item's id; unique across the inbox.</param>
/// <param name="RecipientId">The user it is for.</param>
/// <param name="EventKey">The declared event that made it.</param>
/// <param name="Kind">Notice or action request, as the event was declared when it was raised.</param>
/// <param name="Payload">What the application attached: opaque to this package, stored and returned unchanged.</param>
/// <param name="CreatedAt">When it was raised.</param>
/// <param name="ReadAt">When the recipient first read it; null while unread. Handling an action request counts as reading it.</param>
/// <param name="HandledAt">When the recipient handled the action request; always null for a notice.</param>
public sealed record NotificationItem(
    string Id,
    string RecipientId,
    string EventKey,
    NotificationKind Kind,
    string? Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? HandledAt)
{
    /// <summary>Whether this is an action request nobody has handled yet.</summary>
    public bool IsOpenActionRequest => Kind == NotificationKind.ActionRequest && HandledAt is null;
}

/// <summary>Which of a recipient's items to list.</summary>
public sealed record NotificationInboxFilter
{
    /// <summary>Only items not read yet.</summary>
    public bool UnreadOnly { get; init; }

    /// <summary>Only action requests not handled yet.</summary>
    public bool OpenActionRequestsOnly { get; init; }

    /// <summary>At most this many items, newest first.</summary>
    public int Take { get; init; } = 100;
}

/// <summary>Why a raised event could not be delivered to someone.</summary>
public enum NotificationUnreachedReason
{
    /// <summary>The administrators' table has no target for the event.</summary>
    NoTargets = 0,

    /// <summary>A target names a kind that no registered <see cref="INotificationTargetResolver"/> handles.</summary>
    UnknownTargetKind = 1,

    /// <summary>The targets resolved to nobody: no active user stands behind them.</summary>
    NoActiveRecipient = 2,
}

/// <summary>An event that, or part of which, reached nobody; kept so it is never silently dropped.</summary>
/// <param name="EventKey">The event that was raised.</param>
/// <param name="Reason">Why.</param>
/// <param name="Detail">For <see cref="NotificationUnreachedReason.UnknownTargetKind"/>, the kind nobody handles; otherwise null. Never a payload value.</param>
/// <param name="OccurredAt">When the event was raised.</param>
public sealed record UnreachedNotification(string EventKey, NotificationUnreachedReason Reason, string? Detail, DateTimeOffset OccurredAt);

/// <summary>What raising an event did.</summary>
/// <param name="EventKey">The event that was raised.</param>
/// <param name="Kind">Notice or action request.</param>
/// <param name="Recipients">The users an item was put in front of.</param>
/// <param name="Unreached">What could not be delivered, each also recorded in the <see cref="INotificationUnreachedLog"/>. Empty when everything was.</param>
public sealed record NotificationRaiseResult(
    string EventKey,
    NotificationKind Kind,
    IReadOnlyList<string> Recipients,
    IReadOnlyList<UnreachedNotification> Unreached)
{
    /// <summary>Whether at least one person was reached.</summary>
    public bool Reached => Recipients.Count > 0;
}

/// <summary>What marking an item handled did.</summary>
public enum NotificationHandleResult
{
    /// <summary>The action request is now handled (or already was).</summary>
    Handled = 0,

    /// <summary>The recipient has no such item.</summary>
    NotFound = 1,

    /// <summary>The item is a notice. A notice is read, never handled.</summary>
    NotAnActionRequest = 2,
}
