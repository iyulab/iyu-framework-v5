using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.Notifications;

/// <summary>An event was raised that the application never declared.</summary>
/// <param name="eventKey">The key that was raised.</param>
public sealed class UndeclaredNotificationEventException(string eventKey)
    : InvalidOperationException($"The notification event '{eventKey}' was not declared with IyuNotificationsOptions.Event.")
{
    /// <summary>The key that was raised.</summary>
    public string EventKey { get; } = eventKey;
}

/// <summary>
/// Raises declared events to the people they are for, and answers what those people do with their inboxes.
/// </summary>
/// <remarks>
/// <para>
/// Raising an event reads the administrators' table for the event's targets, resolves each to users, deduplicates them and puts
/// one item in each inbox. An event that reached nobody — no targets, a target kind nobody handles, targets that resolve to no
/// active user — is never dropped silently: it is recorded in the <see cref="INotificationUnreachedLog"/> and returned in
/// <see cref="NotificationRaiseResult.Unreached"/> for the caller to see.
/// </para>
/// <para>
/// The service is scoped. When the ports are the Entity Framework adapter's, they write through the application's own context, so
/// an item is saved in the same unit of work as the change that raised it.
/// </para>
/// </remarks>
public sealed class IyuNotificationService(
    IOptions<IyuNotificationsOptions> options,
    INotificationTargetSource targets,
    INotificationIdentityDirectory identityDirectory,
    IEnumerable<INotificationTargetResolver> resolvers,
    INotificationInbox inbox,
    INotificationUnreachedLog unreachedLog,
    IEnumerable<INotificationDelivery> deliveries,
    TimeProvider time,
    ILogger<IyuNotificationService> logger)
{
    private readonly IReadOnlyList<INotificationTargetResolver> _resolvers = resolvers.ToList();

    // The identity kinds are answered by this package from the host's directory, but only when no application resolver claims the
    // kind: an application resolver wins whatever order the registrations were made in.
    private readonly IdentityTargetResolver _identity = new(identityDirectory);
    private readonly IReadOnlyList<INotificationDelivery> _deliveries = deliveries.ToList();

    /// <summary>
    /// Raises <paramref name="eventKey"/>. <paramref name="payload"/> is attached to every item unchanged; this package never reads it.
    /// </summary>
    /// <param name="eventKey">A declared event.</param>
    /// <param name="payload">What the application wants the recipient's screen to have, opaque to this package; null for none.</param>
    /// <param name="cancellationToken">Cancels the raise.</param>
    /// <exception cref="UndeclaredNotificationEventException">The key was never declared.</exception>
    public async Task<NotificationRaiseResult> RaiseAsync(
        string eventKey, string? payload = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        var declared = options.Value.Events.FirstOrDefault(e => string.Equals(e.Key, eventKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new UndeclaredNotificationEventException(eventKey);

        var now = time.GetUtcNow();
        var unreached = new List<UnreachedNotification>();
        var recipients = new HashSet<string>(StringComparer.Ordinal);

        var wanted = await targets.TargetsAsync(declared.Key, cancellationToken);
        if (wanted.Count == 0)
            unreached.Add(new UnreachedNotification(declared.Key, NotificationUnreachedReason.NoTargets, null, now));

        foreach (var target in wanted)
        {
            var resolver = _resolvers.LastOrDefault(r => r.Handles(target.Kind))
                ?? (_identity.Handles(target.Kind) ? _identity : null);
            if (resolver is null)
            {
                // The kind is the application's own vocabulary, so it can be named; the target's value is not repeated.
                unreached.Add(new UnreachedNotification(declared.Key, NotificationUnreachedReason.UnknownTargetKind, target.Kind, now));
                continue;
            }

            foreach (var user in await resolver.ResolveAsync(target, cancellationToken))
                if (!string.IsNullOrEmpty(user)) recipients.Add(user);
        }

        if (recipients.Count == 0 && wanted.Count > 0 && unreached.Count == 0)
            unreached.Add(new UnreachedNotification(declared.Key, NotificationUnreachedReason.NoActiveRecipient, null, now));

        var items = recipients
            .Select(user => new NotificationItem(
                Guid.CreateVersion7(now).ToString("N"), user, declared.Key, declared.Kind, payload, now, ReadAt: null, HandledAt: null))
            .ToList();
        if (items.Count > 0) await inbox.AddAsync(items, cancellationToken);
        if (unreached.Count > 0)
        {
            await unreachedLog.AddAsync(unreached, cancellationToken);
            logger.LogWarning(
                "Notification event {EventKey} could not be delivered to everyone it was meant for: {Reasons}.",
                declared.Key, string.Join(", ", unreached.Select(u => u.Reason.ToString())));
        }

        await DeliverAsync(items, cancellationToken);
        return new NotificationRaiseResult(declared.Key, declared.Kind, items.Select(i => i.RecipientId).ToList(), unreached);
    }

    /// <summary>The recipient's items, newest first.</summary>
    /// <param name="recipientId">The user.</param>
    /// <param name="filter">Which items; all of the newest 100 when null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<IReadOnlyList<NotificationItem>> ListAsync(
        string recipientId, NotificationInboxFilter? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        return inbox.ListAsync(recipientId, filter ?? new NotificationInboxFilter(), cancellationToken);
    }

    /// <summary>Marks one of the recipient's items read. Returns false when the recipient has no such item.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="recipientId">The user.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task<bool> MarkReadAsync(string itemId, string recipientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        return inbox.MarkReadAsync(itemId, recipientId, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Marks one of the recipient's action requests handled. A notice cannot be handled — it is read — so asking is refused with
    /// <see cref="NotificationHandleResult.NotAnActionRequest"/> instead of silently succeeding.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="recipientId">The user.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<NotificationHandleResult> MarkHandledAsync(
        string itemId, string recipientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);

        var item = await inbox.FindAsync(itemId, recipientId, cancellationToken);
        if (item is null) return NotificationHandleResult.NotFound;
        if (item.Kind != NotificationKind.ActionRequest) return NotificationHandleResult.NotAnActionRequest;

        return await inbox.MarkHandledAsync(itemId, recipientId, time.GetUtcNow(), cancellationToken)
            ? NotificationHandleResult.Handled
            : NotificationHandleResult.NotFound;
    }

    /// <summary>Events that reached nobody, newest first.</summary>
    /// <param name="take">At most this many.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<IReadOnlyList<UnreachedNotification>> UnreachedAsync(int take = 100, CancellationToken cancellationToken = default) =>
        unreachedLog.ListAsync(take, cancellationToken);

    private async Task DeliverAsync(IReadOnlyList<NotificationItem> items, CancellationToken cancellationToken)
    {
        foreach (var delivery in _deliveries)
            foreach (var item in items)
            {
                try
                {
                    await delivery.DeliverAsync(item, cancellationToken);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // The item is already in the inbox; a failed mail or push must not undo it or fail the code that raised the event.
                    logger.LogError(
                        failure, "Notification delivery {Delivery} failed for event {EventKey}; the inbox item was kept.",
                        delivery.GetType().Name, item.EventKey);
                }
            }
    }
}
