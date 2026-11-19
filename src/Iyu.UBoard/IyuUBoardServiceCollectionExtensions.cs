using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Iyu.UBoard;

/// <summary>What <c>AddIyuUBoard</c> turns on. Every part is off until the host names it.</summary>
public sealed class IyuUBoardOptions
{
    /// <summary>Whether the board store is on.</summary>
    public bool BoardsEnabled { get; private set; }

    /// <summary>
    /// Turns on the board store: <see cref="IyuBoardService"/> over the host's <see cref="IBoardStore"/>, for a host that keeps the
    /// boards of an embedded viewer and editor itself.
    /// </summary>
    public IyuUBoardOptions Boards()
    {
        BoardsEnabled = true;
        return this;
    }
}

internal sealed class IyuUBoardOptionsValidator : IValidateOptions<IyuUBoardOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuUBoardOptions options) =>
        options.BoardsEnabled
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("turn on at least one part of the layer, for example IyuUBoardOptions.Boards");
}

/// <summary>Turns the board layer on for a host.</summary>
public static class IyuUBoardServiceCollectionExtensions
{
    /// <summary>
    /// Turns on the parts <paramref name="configure"/> names. The layer is explicit: nothing is registered until this is called,
    /// a part that is not named registers nothing, and a host that names a part without what the part needs does not start. The
    /// message names what is missing, never a value.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Names the parts: <see cref="IyuUBoardOptions.Boards"/>.</param>
    /// <remarks>
    /// <see cref="IyuUBoardOptions.Boards"/> needs an <see cref="IBoardStore"/>. The Entity Framework adapter provides one over the
    /// application's own tables; a host that keeps boards some other way registers its own.
    /// </remarks>
    public static IServiceCollection AddIyuUBoard(this IServiceCollection services, Action<IyuUBoardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var chosen = new IyuUBoardOptions();
        configure(chosen);

        services.TryAddSingleton(TimeProvider.System);
        services.Configure(configure);
        services.AddSingleton<IValidateOptions<IyuUBoardOptions>, IyuUBoardOptionsValidator>();
        services.AddOptions<IyuUBoardOptions>().ValidateOnStart();

        if (chosen.BoardsEnabled)
        {
            services.TryAddScoped<IyuBoardService>();
            services.AddHostedService<IyuUBoardPortsCheck>();
        }

        return services;
    }
}

/// <summary>
/// Fails the host's start when a named part's store was never registered, so a missing store is a message at start and not an
/// error from the first request that reaches for a board.
/// </summary>
internal sealed class IyuUBoardPortsCheck : IHostedService
{
    public IyuUBoardPortsCheck(IServiceProvider services)
    {
        // The store may depend on scoped services (a DbContext), so it is looked for in a scope, not on the root.
        using var scope = services.CreateScope();
        if (scope.ServiceProvider.GetService<IBoardStore>() is not null) return;

        throw new InvalidOperationException(
            "AddIyuUBoard with Boards() needs the host to register an IBoardStore (the Entity Framework adapter keeps boards in the application's own tables).");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
