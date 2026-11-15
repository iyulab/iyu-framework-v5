using Iyu.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Notifications;

/// <summary>
/// Raising an event puts an item in front of each person the administrators' table names, and an event that reaches nobody is
/// never silently dropped.
/// </summary>
public sealed class IyuNotificationServiceTests
{
    private readonly MemoryInbox _inbox = new();
    private readonly MemoryTargets _targets = new();
    private readonly MemoryUnreachedLog _unreached = new();
    private readonly MemoryDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly List<INotificationTargetResolver> _extraResolvers = [];
    private readonly List<INotificationDelivery> _deliveries = [];

    private ServiceProvider Start()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<INotificationInbox>(_inbox);
        services.AddSingleton<INotificationTargetSource>(_targets);
        services.AddSingleton<INotificationUnreachedLog>(_unreached);
        services.AddSingleton<INotificationIdentityDirectory>(_directory);
        foreach (var resolver in _extraResolvers) services.AddSingleton(resolver);
        foreach (var delivery in _deliveries) services.AddSingleton(delivery);
        services.AddIyuNotifications(o =>
        {
            o.Event("work-order.delayed", NotificationKind.Notice, "Work order delayed");
            o.Event("quality.hold", NotificationKind.ActionRequest);
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<T> InScope<T>(ServiceProvider provider, Func<IyuNotificationService, Task<T>> work)
    {
        using var scope = provider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IyuNotificationService>());
    }

    [Fact]
    public async Task A_role_and_a_user_target_put_one_item_in_each_active_recipients_inbox_once()
    {
        _directory.User("ana", true, "planner").User("bo", true, "planner").User("cy", false, "planner").User("di");
        _targets.Add("work-order.delayed", "role", "planner").Add("work-order.delayed", "user", "ana").Add("work-order.delayed", "user", "di");
        using var provider = Start();

        var result = await InScope(provider, s => s.RaiseAsync("work-order.delayed", payload: """{"order":"WO-1"}"""));

        Assert.True(result.Reached);
        Assert.Empty(result.Unreached);
        Assert.Equal(["ana", "bo", "di"], result.Recipients.Order().ToArray()); // ana is in the role and named once more: one item; cy is inactive
        Assert.Equal(3, _inbox.Items.Count);
        Assert.All(_inbox.Items, i =>
        {
            Assert.Equal("work-order.delayed", i.EventKey);
            Assert.Equal(NotificationKind.Notice, i.Kind);
            Assert.Equal("""{"order":"WO-1"}""", i.Payload);
            Assert.Equal(_time.GetUtcNow(), i.CreatedAt);
            Assert.Null(i.ReadAt);
        });
        Assert.Empty(_unreached.Entries);
    }

    [Fact]
    public async Task An_event_is_raised_by_its_key_ignoring_case_and_carries_the_declared_kind()
    {
        _directory.User("ana");
        _targets.Add("quality.hold", "user", "ana");
        using var provider = Start();

        var result = await InScope(provider, s => s.RaiseAsync("QUALITY.HOLD"));

        Assert.Equal("quality.hold", result.EventKey);
        Assert.Equal(NotificationKind.ActionRequest, Assert.Single(_inbox.Items).Kind);
    }

    [Fact]
    public async Task An_event_that_was_never_declared_is_refused_and_nothing_is_stored()
    {
        _targets.Add("work-order.deleyed", "user", "ana"); // a typo that code would otherwise raise into a table
        using var provider = Start();

        var failure = await Assert.ThrowsAsync<UndeclaredNotificationEventException>(
            () => InScope(provider, s => s.RaiseAsync("work-order.deleyed")));

        Assert.Equal("work-order.deleyed", failure.EventKey);
        Assert.Empty(_inbox.Items);
        Assert.Empty(_unreached.Entries);
    }

    [Fact]
    public async Task An_event_with_no_target_is_recorded_as_unreached_not_dropped()
    {
        using var provider = Start();

        var result = await InScope(provider, s => s.RaiseAsync("work-order.delayed"));

        Assert.False(result.Reached);
        var entry = Assert.Single(result.Unreached);
        Assert.Equal(NotificationUnreachedReason.NoTargets, entry.Reason);
        Assert.Equal(entry, Assert.Single(_unreached.Entries));
        Assert.Empty(_inbox.Items);
    }

    [Fact]
    public async Task Targets_that_resolve_to_nobody_active_are_recorded_as_unreached()
    {
        _directory.User("cy", active: false, "planner");
        _targets.Add("work-order.delayed", "role", "planner").Add("work-order.delayed", "user", "cy");
        using var provider = Start();

        var result = await InScope(provider, s => s.RaiseAsync("work-order.delayed"));

        Assert.Equal(NotificationUnreachedReason.NoActiveRecipient, Assert.Single(result.Unreached).Reason);
        Assert.Single(_unreached.Entries);
    }

    [Fact]
    public async Task A_target_kind_nobody_handles_is_named_in_the_record_and_the_other_targets_still_deliver()
    {
        _directory.User("ana");
        _targets.Add("work-order.delayed", "department", "welding").Add("work-order.delayed", "user", "ana");
        using var provider = Start();

        var result = await InScope(provider, s => s.RaiseAsync("work-order.delayed"));

        Assert.Equal(["ana"], result.Recipients);
        var entry = Assert.Single(result.Unreached);
        Assert.Equal(NotificationUnreachedReason.UnknownTargetKind, entry.Reason);
        Assert.Equal("department", entry.Detail); // the kind is vocabulary; the value ("welding") is not repeated
        Assert.DoesNotContain("welding", entry.Detail);
    }

    [Fact]
    public async Task An_application_resolver_handles_a_kind_the_package_does_not_understand()
    {
        _directory.User("ana").User("bo");
        _extraResolvers.Add(new DepartmentResolver(new Dictionary<string, string[]> { ["welding"] = ["ana", "bo"] }));
        _targets.Add("work-order.delayed", "department", "welding");
        using var provider = Start();

        var result = await InScope(provider, s => s.RaiseAsync("work-order.delayed"));

        Assert.Equal(["ana", "bo"], result.Recipients.Order().ToArray());
        Assert.Empty(result.Unreached);
    }

    [Fact]
    public async Task A_notice_is_read_and_cannot_be_handled_while_an_action_request_is_handled_and_counts_as_read()
    {
        _directory.User("ana");
        _targets.Add("work-order.delayed", "user", "ana").Add("quality.hold", "user", "ana");
        using var provider = Start();
        await InScope(provider, s => s.RaiseAsync("work-order.delayed"));
        await InScope(provider, s => s.RaiseAsync("quality.hold"));
        var notice = _inbox.Items.Single(i => i.Kind == NotificationKind.Notice);
        var action = _inbox.Items.Single(i => i.Kind == NotificationKind.ActionRequest);

        Assert.Equal(NotificationHandleResult.NotAnActionRequest, await InScope(provider, s => s.MarkHandledAsync(notice.Id, "ana")));
        Assert.Null(_inbox.Items.Single(i => i.Id == notice.Id).HandledAt);
        Assert.True(await InScope(provider, s => s.MarkReadAsync(notice.Id, "ana")));
        Assert.NotNull(_inbox.Items.Single(i => i.Id == notice.Id).ReadAt);
        Assert.Null(_inbox.Items.Single(i => i.Id == action.Id).ReadAt); // reading the notice did not touch the request

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(NotificationHandleResult.Handled, await InScope(provider, s => s.MarkHandledAsync(action.Id, "ana")));
        var handled = _inbox.Items.Single(i => i.Id == action.Id);
        Assert.NotNull(handled.HandledAt);
        Assert.Equal(handled.HandledAt, handled.ReadAt); // handling it is reading it
        Assert.False(handled.IsOpenActionRequest);
    }

    [Fact]
    public async Task Another_users_item_and_an_unknown_item_are_not_found()
    {
        _directory.User("ana");
        _targets.Add("quality.hold", "user", "ana");
        using var provider = Start();
        await InScope(provider, s => s.RaiseAsync("quality.hold"));
        var id = _inbox.Items.Single().Id;

        Assert.Equal(NotificationHandleResult.NotFound, await InScope(provider, s => s.MarkHandledAsync(id, "bo")));
        Assert.Equal(NotificationHandleResult.NotFound, await InScope(provider, s => s.MarkHandledAsync("nope", "ana")));
        Assert.False(await InScope(provider, s => s.MarkReadAsync(id, "bo")));
        Assert.Null(_inbox.Items.Single().HandledAt);
    }

    [Fact]
    public async Task The_inbox_lists_a_recipients_open_requests_and_unread_items_newest_first()
    {
        _directory.User("ana").User("bo");
        _targets.Add("quality.hold", "user", "ana").Add("work-order.delayed", "user", "ana").Add("quality.hold", "user", "bo");
        using var provider = Start();
        await InScope(provider, s => s.RaiseAsync("work-order.delayed"));
        _time.Advance(TimeSpan.FromMinutes(1));
        await InScope(provider, s => s.RaiseAsync("quality.hold"));

        var all = await InScope(provider, s => s.ListAsync("ana"));
        var open = await InScope(provider, s => s.ListAsync("ana", new NotificationInboxFilter { OpenActionRequestsOnly = true }));

        Assert.Equal(["quality.hold", "work-order.delayed"], all.Select(i => i.EventKey).ToArray());
        Assert.Equal(["quality.hold"], open.Select(i => i.EventKey).ToArray());
        Assert.All(all, i => Assert.Equal("ana", i.RecipientId));
    }

    [Fact]
    public async Task A_delivery_that_fails_does_not_undo_the_inbox_item_or_fail_the_raise()
    {
        _directory.User("ana");
        _targets.Add("work-order.delayed", "user", "ana");
        var good = new CountingDelivery();
        _deliveries.Add(new FailingDelivery());
        _deliveries.Add(good);
        using var provider = Start();

        var result = await InScope(provider, s => s.RaiseAsync("work-order.delayed"));

        Assert.True(result.Reached);
        Assert.Single(_inbox.Items);
        Assert.Equal(1, good.Delivered); // the failure of one delivery did not stop the next
    }

    [Fact]
    public async Task Raising_without_a_key_is_an_argument_error()
    {
        using var provider = Start();

        await Assert.ThrowsAsync<ArgumentException>(() => InScope(provider, s => s.RaiseAsync(" ")));
    }

    private sealed class DepartmentResolver(Dictionary<string, string[]> members) : INotificationTargetResolver
    {
        public bool Handles(string targetKind) => targetKind == "department";

        public Task<IReadOnlyCollection<string>> ResolveAsync(NotificationTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(members.TryGetValue(target.Value, out var ids) ? ids : []);
    }

    private sealed class FailingDelivery : INotificationDelivery
    {
        public Task DeliverAsync(NotificationItem item, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the mail server is down");
    }

    private sealed class CountingDelivery : INotificationDelivery
    {
        public int Delivered { get; private set; }

        public Task DeliverAsync(NotificationItem item, CancellationToken cancellationToken = default)
        {
            Delivered++;
            return Task.CompletedTask;
        }
    }
}
