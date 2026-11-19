using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iyu.UBoard.EntityFrameworkCore;

/// <summary>Turns the board store on for a host that keeps its tables in its own Entity Framework context.</summary>
public static class IyuUBoardEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Turns on <see cref="IyuUBoardOptions.Boards"/> and keeps the boards, their revisions and their publication history in the
    /// application's own tables.
    /// </summary>
    /// <typeparam name="TContext">The application's <c>DbContext</c>, which holds the three tables.</typeparam>
    /// <typeparam name="TBoard">The entity of the board table.</typeparam>
    /// <typeparam name="TRevision">The entity of the revision table.</typeparam>
    /// <typeparam name="TPublication">The entity of the publication history table.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Names any further parts; the board store is always on.</param>
    /// <remarks>
    /// Registers <see cref="IBoardStore"/>, scoped like the context, then everything
    /// <see cref="IyuUBoardServiceCollectionExtensions.AddIyuUBoard"/> registers. The host does not start when the context does not map
    /// the three entities with the keys the store relies on: <c>Id</c> for a board, (<c>BoardId</c>, <c>Revision</c>) for a revision and
    /// (<c>BoardId</c>, <c>Sequence</c>) for a publication line — the last two are what keep two saves on one revision from both
    /// succeeding. A context with a retrying execution strategy is supported.
    /// </remarks>
    public static IServiceCollection AddIyuUBoard<TContext, TBoard, TRevision, TPublication>(
        this IServiceCollection services,
        Action<IyuUBoardOptions>? configure = null)
        where TContext : DbContext
        where TBoard : class, IBoardRow, new()
        where TRevision : class, IBoardRevisionRow, new()
        where TPublication : class, IBoardPublicationRow, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IBoardStore, EFBoardStore<TContext, TBoard, TRevision, TPublication>>();
        services.AddHostedService(provider => new EFBoardModelCheck(provider, typeof(TContext), typeof(TBoard), typeof(TRevision), typeof(TPublication)));
        return services.AddIyuUBoard(options =>
        {
            configure?.Invoke(options);
            options.Boards();
        });
    }
}

/// <summary>
/// Fails the host's start when the application's context does not map what the store reads and writes, so a missing table or key is
/// a message at start and not an error from the first request that saves a board.
/// </summary>
internal sealed class EFBoardModelCheck : IHostedService
{
    public EFBoardModelCheck(IServiceProvider services, Type context, Type board, Type revision, Type publication)
    {
        using var scope = services.CreateScope();
        var model = ((DbContext)scope.ServiceProvider.GetRequiredService(context)).Model;
        var problems = new List<string>();

        Check(board, "the board table", ["Id"]);
        Check(revision, "the revision table", ["BoardId", "Revision"]);
        Check(publication, "the publication history table", ["BoardId", "Sequence"]);

        if (problems.Count == 0) return;
        throw new InvalidOperationException("AddIyuUBoard needs the host to " + string.Join("; and to ", problems) + ".");

        void Check(Type entity, string role, string[] key)
        {
            var mapped = model.FindEntityType(entity);
            if (mapped is null)
            {
                problems.Add($"map {entity.Name} in {context.Name} ({role})");
                return;
            }

            // What the store relies on is that the columns are unique, in that order — not that they are the primary key. A table
            // keyed on a surrogate column of its own, with a unique key or index over the pair, holds the same fact: two saves on one
            // revision cannot both be written. A surrogate's value is the database's or EF's to make; the store does not set it.
            var unique = mapped.GetKeys().Select(k => k.Properties)
                .Concat(mapped.GetIndexes().Where(i => i.IsUnique).Select(i => i.Properties));
            if (!unique.Any(columns => columns.Select(p => p.Name).SequenceEqual(key)))
                problems.Add($"give {entity.Name} ({role}) a primary key, unique key or unique index over ({string.Join(", ", key)}) in that order");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
