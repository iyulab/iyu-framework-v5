using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.Notifications.EntityFrameworkCore;

/// <summary>Turns notification routing on for a host that keeps its tables in its own Entity Framework context.</summary>
public static class IyuNotificationsEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Declares the events <paramref name="configure"/> lists and keeps the inbox, the record of unreached events and the
    /// administrators' target table in the application's own tables.
    /// </summary>
    /// <typeparam name="TContext">The application's <c>DbContext</c>, which holds the three tables.</typeparam>
    /// <typeparam name="TInbox">The entity of the inbox table.</typeparam>
    /// <typeparam name="TUnreached">The entity of the table of events that reached nobody.</typeparam>
    /// <typeparam name="TTarget">The entity of the administrators' target table.</typeparam>
    /// <remarks>
    /// Registers the three stores, scoped like the context, then everything
    /// <see cref="IyuNotificationsServiceCollectionExtensions.AddIyuNotifications"/> registers. A host that keeps its tables some
    /// other way calls that overload and registers its own ports. The host still registers an
    /// <see cref="INotificationIdentityDirectory"/>, and does not start when the context does not map the three entities with
    /// a single-column key on <c>Id</c> (the target table needs none). The inbox listing and the purge order and compare
    /// timestamps in SQL, which SQLite cannot do for a <c>DateTimeOffset</c> column: a host on SQLite maps those columns as a
    /// number (<c>DateTimeOffsetToBinaryConverter</c>); SQL Server and PostgreSQL need nothing.
    /// </remarks>
    public static IServiceCollection AddIyuNotifications<TContext, TInbox, TUnreached, TTarget>(
        this IServiceCollection services,
        Action<IyuNotificationsOptions> configure)
        where TContext : DbContext
        where TInbox : class, INotificationInboxRow, new()
        where TUnreached : class, INotificationUnreachedRow, new()
        where TTarget : class, INotificationTargetRow, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddScoped<INotificationInbox, EFNotificationInbox<TContext, TInbox>>();
        services.AddScoped<INotificationUnreachedLog, EFNotificationUnreachedLog<TContext, TUnreached>>();
        services.AddScoped<INotificationTargetSource, EFNotificationTargetSource<TContext, TTarget>>();
        services.AddHostedService(provider => new EFNotificationModelCheck(provider, typeof(TContext), typeof(TInbox), typeof(TUnreached), typeof(TTarget)));
        return services.AddIyuNotifications(configure);
    }
}

/// <summary>
/// Fails the host's start when the application's context does not map what the stores read and write, so a missing table is a
/// message at start and not an error from the first request that raises an event.
/// </summary>
internal sealed class EFNotificationModelCheck : IHostedService
{
    public EFNotificationModelCheck(IServiceProvider services, Type context, Type inbox, Type unreached, Type target)
    {
        using var scope = services.CreateScope();
        var model = ((DbContext)scope.ServiceProvider.GetRequiredService(context)).Model;
        var problems = new List<string>();

        foreach (var (entity, role, needsKey) in new[] { (inbox, "the inbox", true), (unreached, "the unreached-event record", true), (target, "the target table", false) })
        {
            var mapped = model.FindEntityType(entity);
            if (mapped is null)
            {
                problems.Add($"map {entity.Name} in {context.Name} ({role})");
                continue;
            }

            if (needsKey)
            {
                var key = mapped.FindPrimaryKey();
                if (key is null || key.Properties.Count != 1 || key.Properties[0].Name != "Id")
                    problems.Add($"give {entity.Name} ({role}) a single-column key on Id");
            }
        }

        if (problems.Count == 0) return;
        throw new InvalidOperationException("AddIyuNotifications needs the host to " + string.Join("; and to ", problems) + ".");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
