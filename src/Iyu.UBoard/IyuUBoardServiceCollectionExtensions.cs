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

    /// <summary>Whether the source catalog route is on.</summary>
    public bool CatalogEnabled { get; private set; }

    /// <summary>Whether the catalog lists what the caller may not read, marked as not readable; meaningful when <see cref="CatalogEnabled"/> is true.</summary>
    public bool ShowUnreadable { get; private set; }

    /// <summary>Whether the board screen is served: a page for the list of boards and one for each board, under <c>{prefix}/ui</c>.</summary>
    public bool ScreenEnabled { get; private set; }

    /// <summary>
    /// Where the host serves its OData, as a browser asks for it (an absolute path such as <c>/$data</c>): what the screen's boards read their
    /// values from. Meaningful when <see cref="ScreenEnabled"/> is true.
    /// </summary>
    public string DataRoot { get; private set; } = "/$data";

    /// <summary>
    /// The authorization policy the routes require, by name. When it is left empty the routes require a signed-in caller; there
    /// is no anonymous mode.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// Turns on the board store: <see cref="IyuBoardService"/> over the host's <see cref="IBoardStore"/>, for a host that keeps the
    /// boards of an embedded viewer and editor itself.
    /// </summary>
    public IyuUBoardOptions Boards()
    {
        BoardsEnabled = true;
        return this;
    }

    /// <summary>
    /// Turns on the board screen: <c>GET {prefix}/ui</c> (the boards the caller may open, and a new one) and <c>GET {prefix}/ui/boards/{id}</c>
    /// (one board's viewer, assistant, history and publishing), served from the files this package carries. It needs <see cref="Boards"/>.
    /// </summary>
    /// <param name="dataRoot">
    /// Where the host serves its OData, as a browser asks for it: the screen's boards read their values from it, and the assistant is told
    /// to write that path into them. It is not the host's to pick per request, so it is an absolute path without a host.
    /// </param>
    /// <remarks>
    /// The screen is built from web sources when this package is built. A build of the package without Node.js has no screen: the host
    /// still starts (and logs it), and the screen's routes answer 503 with a message that says so. The package that is published always has it.
    /// </remarks>
    public IyuUBoardOptions Screen(string dataRoot = "/$data")
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !dataRoot.StartsWith('/') || dataRoot.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("The data root is an absolute path such as /$data.", nameof(dataRoot));
        ScreenEnabled = true;
        DataRoot = dataRoot.TrimEnd('/');
        return this;
    }

    /// <summary>
    /// Turns on the source catalog: <c>GET {prefix}/catalog</c>, which lists the entities, properties and links a board can be built
    /// from, as the signed-in caller may read them. It needs an <see cref="IBoardSourceCatalog"/> registered by the host (the
    /// <c>Iyu.UBoard.MainServer</c> adapter describes an <c>Iyu.MainServer</c> host's OData model).
    /// </summary>
    /// <param name="showUnreadable">
    /// False (the default): what the caller may not read is not listed. True: it is listed and marked <c>readable: false</c>, with no
    /// word about which policy guards it — a host opts in because telling a person why they cannot build something is worth the
    /// little it says about the host's rules.
    /// </param>
    public IyuUBoardOptions Catalog(bool showUnreadable = false)
    {
        CatalogEnabled = true;
        ShowUnreadable = showUnreadable;
        return this;
    }
}

internal sealed class IyuUBoardOptionsValidator : IValidateOptions<IyuUBoardOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuUBoardOptions options)
    {
        if (!options.BoardsEnabled && !options.CatalogEnabled)
            return ValidateOptionsResult.Fail("turn on at least one part of the layer, for example IyuUBoardOptions.Boards");
        if (options.ScreenEnabled && !options.BoardsEnabled)
            return ValidateOptionsResult.Fail("the board screen shows boards: turn on IyuUBoardOptions.Boards with IyuUBoardOptions.Screen");

        return ValidateOptionsResult.Success;
    }
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
    /// <param name="configure">Names the parts: <see cref="IyuUBoardOptions.Boards"/>, <see cref="IyuUBoardOptions.Catalog"/>, <see cref="IyuUBoardOptions.Screen"/>.</param>
    /// <remarks>
    /// <see cref="IyuUBoardOptions.Boards"/> needs an <see cref="IBoardAccess"/> (the application's rules for who may do what to which board; the layer fails closed without one) and an <see cref="IBoardStore"/>. The Entity Framework adapter provides one over the
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

        if (chosen.CatalogEnabled) services.AddHostedService<IyuUBoardCatalogCheck>();

        if (chosen.ScreenEnabled)
        {
            // The files are looked up through a port of their own so a test can hand in a screen, or none.
            services.TryAddSingleton<IScreenAssets, EmbeddedScreenAssets>();
            services.AddHostedService<IyuUBoardScreenCheck>();
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
        if (scope.ServiceProvider.GetService<IBoardStore>() is null)
            throw new InvalidOperationException(
                "AddIyuUBoard with Boards() needs the host to register an IBoardStore (the Entity Framework adapter keeps boards in the application's own tables).");

        // Who may do what to which board is the application's to say, and silence is not permission: the layer fails closed.
        if (scope.ServiceProvider.GetService<IBoardAccess>() is null)
            throw new InvalidOperationException(
                "AddIyuUBoard with Boards() needs the host to register an IBoardAccess — who may view, make, edit, publish and delete which board. "
                + "A host with no board-level rules registers EveryoneSignedInBoardAccess by name.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
