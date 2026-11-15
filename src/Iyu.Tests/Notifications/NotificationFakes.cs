using Iyu.Notifications;

namespace Iyu.Tests.Notifications;

/// <summary>An in-memory inbox that honours the one invariant the port states: an open action request is never purged.</summary>
internal sealed class MemoryInbox : INotificationInbox
{
    public List<NotificationItem> Items { get; } = [];

    public Task AddAsync(IReadOnlyList<NotificationItem> items, CancellationToken cancellationToken = default)
    {
        Items.AddRange(items);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NotificationItem>> ListAsync(
        string recipientId, NotificationInboxFilter filter, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationItem>>(Items
            .Where(i => i.RecipientId == recipientId)
            .Where(i => !filter.UnreadOnly || i.ReadAt is null)
            .Where(i => !filter.OpenActionRequestsOnly || i.IsOpenActionRequest)
            .OrderByDescending(i => i.CreatedAt)
            .Take(filter.Take)
            .ToList());

    public Task<NotificationItem?> FindAsync(string itemId, string recipientId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(i => i.Id == itemId && i.RecipientId == recipientId));

    public Task<bool> MarkReadAsync(string itemId, string recipientId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var index = Items.FindIndex(i => i.Id == itemId && i.RecipientId == recipientId);
        if (index < 0) return Task.FromResult(false);
        Items[index] = Items[index] with { ReadAt = Items[index].ReadAt ?? at };
        return Task.FromResult(true);
    }

    public Task<bool> MarkHandledAsync(string itemId, string recipientId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var index = Items.FindIndex(i => i.Id == itemId && i.RecipientId == recipientId);
        if (index < 0) return Task.FromResult(false);
        Items[index] = Items[index] with { ReadAt = Items[index].ReadAt ?? at, HandledAt = Items[index].HandledAt ?? at };
        return Task.FromResult(true);
    }

    public Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.RemoveAll(i => i.Kind == NotificationKind.Notice
            ? i.CreatedAt < cutoff
            : i.HandledAt is { } handled && handled < cutoff));
}

internal sealed class MemoryTargets : INotificationTargetSource
{
    public Dictionary<string, List<NotificationTarget>> ByEvent { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MemoryTargets Add(string eventKey, string kind, string value)
    {
        if (!ByEvent.TryGetValue(eventKey, out var list)) ByEvent[eventKey] = list = [];
        list.Add(new NotificationTarget(kind, value));
        return this;
    }

    public Task<IReadOnlyList<NotificationTarget>> TargetsAsync(string eventKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationTarget>>(ByEvent.TryGetValue(eventKey, out var list) ? list : []);

    public Task<IReadOnlyList<string>> EventKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(ByEvent.Where(e => e.Value.Count > 0).Select(e => e.Key).ToList());
}

internal sealed class MemoryUnreachedLog : INotificationUnreachedLog
{
    public List<UnreachedNotification> Entries { get; } = [];

    public Task AddAsync(IReadOnlyList<UnreachedNotification> entries, CancellationToken cancellationToken = default)
    {
        Entries.AddRange(entries);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UnreachedNotification>> ListAsync(int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UnreachedNotification>>(Entries.OrderByDescending(e => e.OccurredAt).Take(take).ToList());
}

/// <summary>Users and roles as a host's accounts would answer: a role lists its members, an inactive user is in none.</summary>
internal sealed class MemoryDirectory : INotificationIdentityDirectory
{
    public HashSet<string> Active { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<string>> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MemoryDirectory User(string id, bool active = true, params string[] roles)
    {
        if (active) Active.Add(id);
        foreach (var role in roles)
        {
            if (!Roles.TryGetValue(role, out var members)) Roles[role] = members = [];
            members.Add(id);
        }

        return this;
    }

    public Task<bool> IsActiveUserAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Active.Contains(userId));

    public Task<IReadOnlyCollection<string>> ActiveUserIdsInRoleAsync(string role, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(
            Roles.TryGetValue(role, out var members) ? members.Where(Active.Contains).ToList() : []);
}
