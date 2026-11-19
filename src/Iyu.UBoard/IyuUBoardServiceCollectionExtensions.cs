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

    /// <summary>Whether the model proxy is on.</summary>
    public bool ModelEnabled { get; private set; }

    /// <summary>The model service the proxy talks to; meaningful when <see cref="ModelEnabled"/> is true.</summary>
    public IyuUBoardModelOptions ModelSettings { get; } = new();

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
    /// Turns on the model proxy: <c>POST {prefix}/model/chat/completions</c>, which sends an assistant's request to the model
    /// service named by <paramref name="configure"/> with the server's key. See <see cref="IyuUBoardModelOptions"/>.
    /// </summary>
    public IyuUBoardOptions Model(Action<IyuUBoardModelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ModelEnabled = true;
        configure(ModelSettings);
        return this;
    }
}

internal sealed class IyuUBoardOptionsValidator : IValidateOptions<IyuUBoardOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuUBoardOptions options)
    {
        if (!options.BoardsEnabled && !options.ModelEnabled)
            return ValidateOptionsResult.Fail("turn on at least one part of the layer, for example IyuUBoardOptions.Boards");

        if (!options.ModelEnabled) return ValidateOptionsResult.Success;

        // Name what is missing, never a value: a key that is wrong must not reach a log.
        var model = options.ModelSettings;
        var missing = new List<string>();
        if (model.BaseUrl is null || !model.BaseUrl.IsAbsoluteUri || model.BaseUrl.Scheme is not ("http" or "https"))
            missing.Add("BaseUrl (an absolute http or https address)");
        if (string.IsNullOrWhiteSpace(model.ApiKey)) missing.Add("ApiKey");
        if (string.IsNullOrWhiteSpace(model.ModelName)) missing.Add("ModelName");
        if (model.MaxRequestBytes <= 0) missing.Add("MaxRequestBytes (greater than zero)");
        if (model.MaxOutputTokens <= 0) missing.Add("MaxOutputTokens (greater than zero)");
        if (model.OutputLimitField is not ("max_tokens" or "max_completion_tokens")) missing.Add("OutputLimitField (max_tokens or max_completion_tokens)");
        if (model.Timeout <= TimeSpan.Zero) missing.Add("Timeout (greater than zero)");

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("IyuUBoardOptions.Model needs: " + string.Join(", ", missing));
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
    /// <param name="configure">Names the parts: <see cref="IyuUBoardOptions.Boards"/>, <see cref="IyuUBoardOptions.Model"/>.</param>
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

        if (chosen.ModelEnabled)
        {
            // The host owns the handler (proxy, certificates, test doubles): it configures this named client like any other.
            services.AddHttpClient(IyuUBoardEndpoints.ModelClientName, (provider, client) =>
                client.Timeout = provider.GetRequiredService<IOptions<IyuUBoardOptions>>().Value.ModelSettings.Timeout);
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
