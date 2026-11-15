using Iyu.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Notifications;

/// <summary>
/// The layer is explicit and fails closed: a host that turns it on with a bad declaration or without the stores it needs does
/// not start, and the message says which piece is missing. An inbox keeps everything unless the application gives a retention,
/// and an open action request survives any clean-up.
/// </summary>
public sealed class AddIyuNotificationsTests
{
    private static HostApplicationBuilder Host() =>
        Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });

    private static void Ports(IServiceCollection services, bool inbox = true, bool targets = true, bool log = true, bool directory = true)
    {
        if (inbox) services.AddSingleton<INotificationInbox>(new MemoryInbox());
        if (targets) services.AddSingleton<INotificationTargetSource>(new MemoryTargets());
        if (log) services.AddSingleton<INotificationUnreachedLog>(new MemoryUnreachedLog());
        if (directory) services.AddSingleton<INotificationIdentityDirectory>(new MemoryDirectory());
    }

    private static async Task<Exception> StartFails(Action<HostApplicationBuilder> arrange)
    {
        var builder = Host();
        arrange(builder);
        using var app = builder.Build();
        return await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
    }

    [Fact]
    public async Task A_host_with_an_event_and_every_port_starts()
    {
        var builder = Host();
        Ports(builder.Services);
        builder.Services.AddIyuNotifications(o => o.Event("work-order.delayed", NotificationKind.Notice));
        using var app = builder.Build();

        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task The_ports_may_be_registered_after_the_layer_is_turned_on()
    {
        var builder = Host();
        builder.Services.AddIyuNotifications(o => o.Event("work-order.delayed", NotificationKind.Notice));
        Ports(builder.Services);
        using var app = builder.Build();

        await app.StartAsync();
        await app.StopAsync();
    }

    [Theory]
    [InlineData("inbox", "INotificationInbox")]
    [InlineData("targets", "INotificationTargetSource")]
    [InlineData("log", "INotificationUnreachedLog")]
    [InlineData("directory", "INotificationIdentityDirectory")]
    public async Task A_host_missing_a_port_does_not_start_and_the_message_names_it(string missing, string expected)
    {
        var failure = await StartFails(b =>
        {
            Ports(b.Services, inbox: missing != "inbox", targets: missing != "targets", log: missing != "log", directory: missing != "directory");
            b.Services.AddIyuNotifications(o => o.Event("work-order.delayed", NotificationKind.Notice));
        });

        Assert.Contains(expected, failure.Message);
    }

    [Fact]
    public async Task A_target_row_for_a_declared_event_lets_the_host_start()
    {
        var builder = Host();
        Ports(builder.Services, targets: false);
        builder.Services.AddSingleton<INotificationTargetSource>(new MemoryTargets().Add("work-order.delayed", "role", "planner"));
        builder.Services.AddIyuNotifications(o => o.Event("work-order.delayed", NotificationKind.Notice));
        using var app = builder.Build();

        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task A_target_row_for_an_event_nothing_declares_stops_the_host_and_names_the_key_but_not_the_target()
    {
        var failure = await StartFails(b =>
        {
            Ports(b.Services, targets: false);
            b.Services.AddSingleton<INotificationTargetSource>(
                new MemoryTargets().Add("work-order.delayed", "role", "planner").Add("work-order.delyed", "user", "secret-user"));
            b.Services.AddIyuNotifications(o => o.Event("work-order.delayed", NotificationKind.Notice));
        });

        Assert.Contains("'work-order.delyed'", failure.Message);
        Assert.DoesNotContain("work-order.delayed'", failure.Message);
        Assert.DoesNotContain("secret-user", failure.Message);
    }

    [Fact]
    public async Task A_key_that_differs_only_in_case_counts_as_undeclared_because_the_lookup_uses_the_declared_spelling()
    {
        var failure = await StartFails(b =>
        {
            Ports(b.Services, targets: false);
            b.Services.AddSingleton<INotificationTargetSource>(new MemoryTargets().Add("Work-Order.Delayed", "role", "planner"));
            b.Services.AddIyuNotifications(o => o.Event("work-order.delayed", NotificationKind.Notice));
        });

        Assert.Contains("'Work-Order.Delayed'", failure.Message);
    }

    [Fact]
    public async Task Many_undeclared_keys_are_named_up_to_ten_and_the_rest_counted()
    {
        var failure = await StartFails(b =>
        {
            Ports(b.Services, targets: false);
            var targets = new MemoryTargets();
            for (var i = 0; i < 13; i++) targets.Add($"missing.{i:00}", "role", "planner");
            b.Services.AddSingleton<INotificationTargetSource>(targets);
            b.Services.AddIyuNotifications(o => o.Event("work-order.delayed", NotificationKind.Notice));
        });

        Assert.Contains("'missing.00'", failure.Message);
        Assert.Contains("'missing.09'", failure.Message);
        Assert.DoesNotContain("'missing.10'", failure.Message);
        Assert.Contains("and 3 more", failure.Message);
    }

    [Fact]
    public async Task A_host_with_no_event_does_not_start()
    {
        var failure = await StartFails(b =>
        {
            Ports(b.Services);
            b.Services.AddIyuNotifications(_ => { });
        });

        Assert.Contains("at least one event", failure.Message);
    }

    [Fact]
    public async Task Blank_keys_and_names_are_all_reported_at_start()
    {
        var failure = await StartFails(b =>
        {
            Ports(b.Services);
            b.Services.AddIyuNotifications(o =>
            {
                o.Event(" ", NotificationKind.Notice);
                o.Event("work-order.delayed", NotificationKind.Notice, name: " ");
                o.Event(" padded ", NotificationKind.Notice);
                o.Event(new string('k', IyuNotificationsOptions.MaxTextLength + 1), NotificationKind.Notice);
            });
        });

        Assert.Contains("an event has an empty key", failure.Message);
        Assert.Contains("the event 'work-order.delayed' has an empty name", failure.Message);
        Assert.Contains("has leading or trailing spaces", failure.Message);
        Assert.Contains("longer than", failure.Message);
    }

    [Fact]
    public void A_key_declared_twice_ignoring_case_is_refused_where_it_is_declared()
    {
        var options = new IyuNotificationsOptions();
        options.Event("work-order.delayed", NotificationKind.Notice);

        var failure = Assert.Throws<ArgumentException>(() => options.Event("Work-Order.Delayed", NotificationKind.ActionRequest));

        Assert.Contains("already declared", failure.Message);
        Assert.Single(options.Events);
    }

    [Fact]
    public async Task A_retention_that_is_not_longer_than_zero_does_not_start()
    {
        var failure = await StartFails(b =>
        {
            Ports(b.Services);
            b.Services.AddIyuNotifications(o =>
            {
                o.Event("work-order.delayed", NotificationKind.Notice);
                o.InboxRetention = TimeSpan.Zero;
            });
        });

        Assert.Contains("InboxRetention", failure.Message);
    }

    [Fact]
    public void An_inbox_keeps_everything_by_default()
    {
        Assert.Null(new IyuNotificationsOptions().InboxRetention);
    }

    // Both kinds are purged by age; the invariant is that an action request nobody handled is not.
    [Fact]
    public async Task The_cleanup_removes_old_notices_and_handled_requests_and_never_an_open_action_request()
    {
        var inbox = new MemoryInbox();
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var old = now - TimeSpan.FromDays(400);
        inbox.Items.AddRange([
            new NotificationItem("old-notice", "ana", "n", NotificationKind.Notice, null, old, null, null),
            new NotificationItem("new-notice", "ana", "n", NotificationKind.Notice, null, now - TimeSpan.FromDays(2), null, null),
            new NotificationItem("old-open-request", "ana", "a", NotificationKind.ActionRequest, null, old, old, null),
            new NotificationItem("old-handled-request", "ana", "a", NotificationKind.ActionRequest, null, old, old, old),
            new NotificationItem("recently-handled-request", "ana", "a", NotificationKind.ActionRequest, null, old, old, now - TimeSpan.FromDays(1)),
        ]);
        var time = new FakeTimeProvider(now);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<INotificationInbox>(inbox);
        services.AddSingleton<INotificationTargetSource>(new MemoryTargets());
        services.AddSingleton<INotificationUnreachedLog>(new MemoryUnreachedLog());
        services.AddSingleton<INotificationIdentityDirectory>(new MemoryDirectory());
        services.AddIyuNotifications(o =>
        {
            o.Event("n", NotificationKind.Notice);
            o.InboxRetention = TimeSpan.FromDays(365);
        });
        using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().OfType<IyuNotificationsCleanupWorker>().Single();

        var removed = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Equal(["new-notice", "old-open-request", "recently-handled-request"], inbox.Items.Select(i => i.Id).Order().ToArray());
    }

    [Fact]
    public async Task With_no_retention_the_cleanup_removes_nothing_however_old()
    {
        var inbox = new MemoryInbox();
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        inbox.Items.Add(new NotificationItem("ancient", "ana", "n", NotificationKind.Notice, null, now - TimeSpan.FromDays(9000), null, null));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(now));
        services.AddSingleton<INotificationInbox>(inbox);
        services.AddSingleton<INotificationTargetSource>(new MemoryTargets());
        services.AddSingleton<INotificationUnreachedLog>(new MemoryUnreachedLog());
        services.AddSingleton<INotificationIdentityDirectory>(new MemoryDirectory());
        services.AddIyuNotifications(o => o.Event("n", NotificationKind.Notice));
        using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().OfType<IyuNotificationsCleanupWorker>().Single();

        Assert.Equal(0, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Single(inbox.Items);
    }

    [Fact]
    public void The_neutral_layer_references_neither_Iyu_Core_nor_Entity_Framework_Core()
    {
        var references = typeof(IyuNotificationService).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Iyu.Core", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
