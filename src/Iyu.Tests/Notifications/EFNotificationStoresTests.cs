using Iyu.Notifications;
using Iyu.Notifications.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Notifications;

public class NotifInboxRow : INotificationInboxRow
{
    public string Id { get; set; } = "";

    public string RecipientId { get; set; } = "";

    public string EventKey { get; set; } = "";

    public NotificationKind Kind { get; set; }

    public string? Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset? HandledAt { get; set; }
}

public class NotifUnreachedRow : INotificationUnreachedRow
{
    public string Id { get; set; } = "";

    public string EventKey { get; set; } = "";

    public NotificationUnreachedReason Reason { get; set; }

    public string? Detail { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public class NotifTargetRow : INotificationTargetRow
{
    public int Id { get; set; }

    public string EventKey { get; set; } = "";

    public string TargetKind { get; set; } = "";

    public string TargetValue { get; set; } = "";
}

public class NotifTestDb(DbContextOptions<NotifTestDb> options) : DbContext(options)
{
    public DbSet<NotifInboxRow> Inbox => Set<NotifInboxRow>();

    public DbSet<NotifUnreachedRow> Unreached => Set<NotifUnreachedRow>();

    public DbSet<NotifTargetRow> Targets => Set<NotifTargetRow>();

    // A table of the application's own, in the same context: the routing's writes ride the same unit of work.
    public DbSet<WorkOrderRow> WorkOrders => Set<WorkOrderRow>();

    // SQLite cannot order or compare a DateTimeOffset column in SQL, which the inbox listing and the purge both do. Hosts on
    // SQLite map it as a number, as here; SQL Server and PostgreSQL need nothing.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}

public class WorkOrderRow
{
    public int Id { get; set; }

    public string Title { get; set; } = "";
}

/// <summary>A context that maps none of the notification entities.</summary>
public class NotifBareDb(DbContextOptions<NotifBareDb> options) : DbContext(options)
{
    public DbSet<WorkOrderRow> WorkOrders => Set<WorkOrderRow>();
}

/// <summary>The inbox, the record of unreached events and the target table over the application's own tables.</summary>
public sealed class EFNotificationStoresTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-notifications-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly MemoryDirectory _directory = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private ServiceProvider Start()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<NotifTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddSingleton<INotificationIdentityDirectory>(_directory);
        services.AddIyuNotifications<NotifTestDb, NotifInboxRow, NotifUnreachedRow, NotifTargetRow>(o =>
        {
            o.Event("work-order.delayed", NotificationKind.Notice);
            o.Event("quality.hold", NotificationKind.ActionRequest);
        });
        var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<NotifTestDb>().Database.EnsureCreated();
        return provider;
    }

    private static async Task<T> InScope<T>(ServiceProvider provider, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = provider.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private static Task Seed(ServiceProvider provider, Action<NotifTestDb> add) =>
        InScope(provider, async sp =>
        {
            var db = sp.GetRequiredService<NotifTestDb>();
            add(db);
            await db.SaveChangesAsync();
            return 0;
        });

    private static Task<List<T>> Read<T>(ServiceProvider provider, Func<NotifTestDb, IQueryable<T>> query) where T : class =>
        InScope(provider, sp => query(sp.GetRequiredService<NotifTestDb>()).AsNoTracking().ToListAsync());

    [Fact]
    public async Task An_event_raised_over_the_application_tables_reaches_the_inbox_the_target_table_names()
    {
        _directory.User("ana", true, "planner").User("bo", true, "planner");
        using var provider = Start();
        await Seed(provider, db =>
        {
            db.Targets.Add(new NotifTargetRow { EventKey = "work-order.delayed", TargetKind = "role", TargetValue = "planner" });
            db.Targets.Add(new NotifTargetRow { EventKey = "quality.hold", TargetKind = "user", TargetValue = "ana" });
        });

        var result = await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().RaiseAsync("work-order.delayed", "payload-1"));

        Assert.Equal(["ana", "bo"], result.Recipients.Order().ToArray());
        var rows = await Read(provider, db => db.Inbox);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("work-order.delayed", r.EventKey);
            Assert.Equal(NotificationKind.Notice, r.Kind);
            Assert.Equal("payload-1", r.Payload);
            Assert.Equal(_time.GetUtcNow(), r.CreatedAt);
            Assert.Null(r.ReadAt);
        });
    }

    [Fact]
    public async Task An_event_nobody_could_be_reached_for_is_kept_in_the_applications_own_table()
    {
        using var provider = Start();
        await Seed(provider, db => db.Targets.Add(new NotifTargetRow { EventKey = "work-order.delayed", TargetKind = "department", TargetValue = "welding" }));

        var result = await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().RaiseAsync("work-order.delayed"));
        var listed = await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().UnreachedAsync());

        Assert.False(result.Reached);
        var row = Assert.Single(await Read(provider, db => db.Unreached));
        Assert.Equal(NotificationUnreachedReason.UnknownTargetKind, row.Reason);
        Assert.Equal("department", row.Detail);
        Assert.Equal(_time.GetUtcNow(), row.OccurredAt);
        Assert.Equal(row.EventKey, Assert.Single(listed).EventKey);
        Assert.Empty(await Read(provider, db => db.Inbox));
    }

    [Fact]
    public async Task An_item_raised_inside_the_hosts_transaction_is_rolled_back_with_it()
    {
        _directory.User("ana");
        using var provider = Start();
        await Seed(provider, db => db.Targets.Add(new NotifTargetRow { EventKey = "work-order.delayed", TargetKind = "user", TargetValue = "ana" }));

        await InScope(provider, async sp =>
        {
            var db = sp.GetRequiredService<NotifTestDb>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.WorkOrders.Add(new WorkOrderRow { Title = "WO-1" });
            await db.SaveChangesAsync();
            await sp.GetRequiredService<IyuNotificationService>().RaiseAsync("work-order.delayed");
            await transaction.RollbackAsync();
            return 0;
        });

        Assert.Empty(await Read(provider, db => db.Inbox));
        Assert.Empty(await Read(provider, db => db.WorkOrders));
    }

    [Fact]
    public async Task Reading_and_handling_are_stored_and_keep_the_first_time()
    {
        _directory.User("ana");
        using var provider = Start();
        await Seed(provider, db =>
        {
            db.Targets.Add(new NotifTargetRow { EventKey = "work-order.delayed", TargetKind = "user", TargetValue = "ana" });
            db.Targets.Add(new NotifTargetRow { EventKey = "quality.hold", TargetKind = "user", TargetValue = "ana" });
        });
        await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().RaiseAsync("work-order.delayed"));
        await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().RaiseAsync("quality.hold"));
        var rows = await Read(provider, db => db.Inbox);
        var notice = rows.Single(r => r.Kind == NotificationKind.Notice);
        var request = rows.Single(r => r.Kind == NotificationKind.ActionRequest);
        var firstRead = _time.GetUtcNow() + TimeSpan.FromMinutes(1);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().MarkReadAsync(notice.Id, "ana")));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().MarkReadAsync(notice.Id, "ana")));
        Assert.Equal(NotificationHandleResult.NotAnActionRequest,
            await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().MarkHandledAsync(notice.Id, "ana")));
        var handledAt = _time.GetUtcNow();
        Assert.Equal(NotificationHandleResult.Handled,
            await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().MarkHandledAsync(request.Id, "ana")));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(NotificationHandleResult.Handled,
            await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().MarkHandledAsync(request.Id, "ana")));

        var after = await Read(provider, db => db.Inbox);
        Assert.Equal(firstRead, after.Single(r => r.Id == notice.Id).ReadAt);
        Assert.Null(after.Single(r => r.Id == notice.Id).HandledAt);
        Assert.Equal(handledAt, after.Single(r => r.Id == request.Id).HandledAt);
        Assert.Equal(handledAt, after.Single(r => r.Id == request.Id).ReadAt);
        Assert.Equal(NotificationHandleResult.NotFound,
            await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().MarkHandledAsync(request.Id, "bo")));
    }

    [Fact]
    public async Task The_listing_is_a_recipients_own_newest_first_and_narrows_to_unread_or_open_requests()
    {
        using var provider = Start();
        var t0 = _time.GetUtcNow();
        await Seed(provider, db => db.Inbox.AddRange(
            Row("a-old-notice", "ana", NotificationKind.Notice, t0 - TimeSpan.FromHours(3), readAt: t0),
            Row("b-new-notice", "ana", NotificationKind.Notice, t0 - TimeSpan.FromHours(1)),
            Row("c-open-request", "ana", NotificationKind.ActionRequest, t0 - TimeSpan.FromHours(2)),
            Row("d-done-request", "ana", NotificationKind.ActionRequest, t0 - TimeSpan.FromHours(4), readAt: t0, handledAt: t0),
            Row("e-someone-elses", "bo", NotificationKind.Notice, t0)));

        async Task<string[]> Ids(NotificationInboxFilter? filter) =>
            (await InScope(provider, sp => sp.GetRequiredService<IyuNotificationService>().ListAsync("ana", filter))).Select(i => i.Id).ToArray();

        Assert.Equal(["b-new-notice", "c-open-request", "a-old-notice", "d-done-request"], await Ids(null));
        Assert.Equal(["b-new-notice", "c-open-request"], await Ids(new NotificationInboxFilter { UnreadOnly = true }));
        Assert.Equal(["c-open-request"], await Ids(new NotificationInboxFilter { OpenActionRequestsOnly = true }));
        Assert.Equal(["b-new-notice"], await Ids(new NotificationInboxFilter { Take = 1 }));
    }

    // The invariant the port states, held against a real database: no cut-off reaches a request nobody handled.
    [Fact]
    public async Task The_purge_removes_old_notices_and_handled_requests_and_never_an_open_action_request()
    {
        using var provider = Start();
        var now = _time.GetUtcNow();
        var old = now - TimeSpan.FromDays(400);
        await Seed(provider, db => db.Inbox.AddRange(
            Row("old-notice", "ana", NotificationKind.Notice, old),
            Row("new-notice", "ana", NotificationKind.Notice, now - TimeSpan.FromDays(2)),
            Row("old-open-request", "ana", NotificationKind.ActionRequest, old, readAt: old),
            Row("old-handled-request", "ana", NotificationKind.ActionRequest, old, readAt: old, handledAt: old),
            Row("recently-handled-request", "ana", NotificationKind.ActionRequest, old, readAt: old, handledAt: now - TimeSpan.FromDays(1))));

        var removed = await InScope(provider, sp => sp.GetRequiredService<INotificationInbox>().PurgeAsync(now - TimeSpan.FromDays(365)));
        var everything = await InScope(provider, sp => sp.GetRequiredService<INotificationInbox>().PurgeAsync(now + TimeSpan.FromDays(3650)));

        Assert.Equal(2, removed);
        Assert.Equal(2, everything); // even a cut-off in the far future removes only the new notice and the recently handled request
        Assert.Equal(["old-open-request"], (await Read(provider, db => db.Inbox)).Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_context_that_does_not_map_the_notification_entities_does_not_start()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NotifBareDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddSingleton<INotificationIdentityDirectory>(_directory);
        // The adapter is typed to a context that must map the entities; a context that maps none is the application's mistake.
        services.AddIyuNotifications<NotifBareDb, NotifInboxRow, NotifUnreachedRow, NotifTargetRow>(o => o.Event("work-order.delayed", NotificationKind.Notice));
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToList());

        Assert.Contains("NotifInboxRow in NotifBareDb", failure.Message);
        Assert.Contains("NotifUnreachedRow in NotifBareDb", failure.Message);
        Assert.Contains("NotifTargetRow in NotifBareDb", failure.Message);
    }

    private static NotifInboxRow Row(
        string id, string recipient, NotificationKind kind, DateTimeOffset createdAt, DateTimeOffset? readAt = null, DateTimeOffset? handledAt = null) =>
        new() { Id = id, RecipientId = recipient, EventKey = "e", Kind = kind, CreatedAt = createdAt, ReadAt = readAt, HandledAt = handledAt };
}
