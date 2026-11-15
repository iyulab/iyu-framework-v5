using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.Notifications;

/// <summary>Turns notification routing on for a host.</summary>
public static class IyuNotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Declares the events <paramref name="configure"/> lists and registers <see cref="IyuNotificationService"/>. The layer is
    /// explicit: nothing is registered until this is called, and a host that calls it with no event, a blank or too long key or
    /// name, no <see cref="INotificationTargetSource"/>, <see cref="INotificationInbox"/>, <see cref="INotificationUnreachedLog"/>
    /// or <see cref="INotificationIdentityDirectory"/> does not start. The message names what is missing, never a value.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Declares the events and, optionally, how long an inbox keeps what is finished with.</param>
    /// <remarks>
    /// The Entity Framework adapter provides the three stores over the application's own tables; a host that keeps them some
    /// other way registers its own. The identity directory is always the host's, because only the host knows its accounts.
    /// </remarks>
    public static IServiceCollection AddIyuNotifications(this IServiceCollection services, Action<IyuNotificationsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuNotificationsOptions>, IyuNotificationsOptionsValidator>();
        services.AddOptions<IyuNotificationsOptions>().ValidateOnStart();

        services.TryAddScoped<IyuNotificationService>();
        services.AddHostedService<IyuNotificationsPortsCheck>();
        services.AddHostedService<IyuNotificationsCleanupWorker>();
        return services;
    }
}

/// <summary>Resolves the identity kinds — a user id and a role name — through the host's <see cref="INotificationIdentityDirectory"/>.</summary>
internal sealed class IdentityTargetResolver(INotificationIdentityDirectory directory) : INotificationTargetResolver
{
    public bool Handles(string targetKind) =>
        string.Equals(targetKind, NotificationTargetKinds.User, StringComparison.OrdinalIgnoreCase)
        || string.Equals(targetKind, NotificationTargetKinds.Role, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyCollection<string>> ResolveAsync(NotificationTarget target, CancellationToken cancellationToken = default)
    {
        if (string.Equals(target.Kind, NotificationTargetKinds.Role, StringComparison.OrdinalIgnoreCase))
            return await directory.ActiveUserIdsInRoleAsync(target.Value, cancellationToken);

        return await directory.IsActiveUserAsync(target.Value, cancellationToken) ? [target.Value] : [];
    }
}

/// <summary>
/// Fails the host's start when what the layer needs was never registered, or when the administrators' table names an event
/// nothing declares. The ports are checked in the constructor because that is the first moment the host asks for anything of the
/// layer; the table is read in <see cref="StartAsync"/>. A message here names the missing piece where an error from deep inside a
/// later request would not.
/// </summary>
internal sealed class IyuNotificationsPortsCheck : IHostedService
{
    private const int MaxNamedKeys = 10;

    private readonly IServiceProvider _services;
    private readonly IOptions<IyuNotificationsOptions> _options;

    public IyuNotificationsPortsCheck(IServiceProvider services)
    {
        _services = services;
        _options = services.GetRequiredService<IOptions<IyuNotificationsOptions>>();
        var problems = new List<string>();

        // The stores may depend on scoped services (a DbContext), so they are looked for in a scope, not on the root.
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        if (provider.GetService<INotificationTargetSource>() is null)
            problems.Add("register an INotificationTargetSource (the Entity Framework adapter reads it from the application's table)");
        if (provider.GetService<INotificationInbox>() is null)
            problems.Add("register an INotificationInbox (the Entity Framework adapter keeps it in the application's table)");
        if (provider.GetService<INotificationUnreachedLog>() is null)
            problems.Add("register an INotificationUnreachedLog, so an event nobody could be reached for is never dropped silently");
        if (provider.GetService<INotificationIdentityDirectory>() is null)
            problems.Add("register an INotificationIdentityDirectory that answers who the users and roles are");

        if (problems.Count == 0) return;
        throw new InvalidOperationException("AddIyuNotifications needs the host to " + string.Join("; and to ", problems) + ".");
    }

    /// <summary>
    /// Reads the keys the table uses and compares them, exactly (the key an event is looked up by is the declared one), with the
    /// declared events. Event keys are the application's vocabulary, so they are named; a target's value is not.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> used;
        await using (var scope = _services.CreateAsyncScope())
            used = await scope.ServiceProvider.GetRequiredService<INotificationTargetSource>().EventKeysAsync(cancellationToken);

        var declared = _options.Value.Events.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var undeclared = used.Where(key => !declared.Contains(key)).Order(StringComparer.Ordinal).ToList();
        if (undeclared.Count == 0) return;

        var named = string.Join(", ", undeclared.Take(MaxNamedKeys).Select(key => $"'{key}'"));
        var more = undeclared.Count > MaxNamedKeys ? $" and {undeclared.Count - MaxNamedKeys} more" : "";
        throw new InvalidOperationException(
            $"The notification target table names {(undeclared.Count == 1 ? "an event" : "events")} that AddIyuNotifications does not declare: {named}{more}. " +
            "Declare them with IyuNotificationsOptions.Event, or correct or remove the rows (keys are compared exactly, including case).");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Removes what an inbox no longer needs to keep, when the application gave a retention. With none (the default) it does nothing,
/// and the open action requests are never its to remove: that is the inbox's invariant, not this worker's judgment.
/// </summary>
internal sealed class IyuNotificationsCleanupWorker(
    IServiceScopeFactory scopes,
    IOptions<IyuNotificationsOptions> options,
    TimeProvider time,
    ILogger<IyuNotificationsCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.InboxRetention is null) return;

        using var timer = new PeriodicTimer(options.Value.CleanupInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One clean-up pass; a failure is logged and the next tick tries again.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (options.Value.InboxRetention is not { } retention) return 0;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var removed = await scope.ServiceProvider.GetRequiredService<INotificationInbox>()
                .PurgeAsync(time.GetUtcNow() - retention, cancellationToken);
            if (removed > 0) logger.LogInformation("Notification inbox clean-up removed {Count} item(s) older than {Retention}.", removed, retention);
            return removed;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogError(failure, "Notification inbox clean-up failed; the next tick tries again.");
            return 0;
        }
    }
}
