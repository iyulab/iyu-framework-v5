using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.UBoard.EntityFrameworkCore;

/// <summary>A board in the table the library ships (<c>IyuBoards</c>). A host that wants its own columns writes its own row type instead.</summary>
public class IyuBoardRow : IBoardRow
{
    /// <inheritdoc />
    public string Id { get; set; } = "";

    /// <inheritdoc />
    public string Name { get; set; } = "";

    /// <inheritdoc />
    public string Document { get; set; } = "";

    /// <inheritdoc />
    public int CurrentRevision { get; set; }

    /// <inheritdoc />
    public int? PublishedRevision { get; set; }
}

/// <summary>A revision of a board in the table the library ships (<c>IyuBoardRevisions</c>).</summary>
public class IyuBoardRevisionRow : IBoardRevisionRow
{
    /// <inheritdoc />
    public string BoardId { get; set; } = "";

    /// <inheritdoc />
    public int Revision { get; set; }

    /// <inheritdoc />
    public string Document { get; set; } = "";

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public string CreatedBy { get; set; } = "";
}

/// <summary>A line of a board's publication history in the table the library ships (<c>IyuBoardPublications</c>).</summary>
public class IyuBoardPublicationRow : IBoardPublicationRow
{
    /// <inheritdoc />
    public string BoardId { get; set; } = "";

    /// <inheritdoc />
    public int Sequence { get; set; }

    /// <inheritdoc />
    public int? Revision { get; set; }

    /// <inheritdoc />
    public DateTimeOffset PublishedAt { get; set; }

    /// <inheritdoc />
    public string PublishedBy { get; set; } = "";
}

/// <summary>Maps the tables the library ships for boards, and adds them to a context whose source the host does not own.</summary>
public static class IyuBoardTablesExtensions
{
    /// <summary>
    /// Maps <see cref="IyuBoardRow"/>, <see cref="IyuBoardRevisionRow"/> and <see cref="IyuBoardPublicationRow"/> to the tables
    /// <c>IyuBoards</c>, <c>IyuBoardRevisions</c> and <c>IyuBoardPublications</c>, with the keys the store relies on (<c>Id</c>;
    /// <c>BoardId</c> and <c>Revision</c>; <c>BoardId</c> and <c>Sequence</c>). Call it from <c>OnModelCreating</c> of a context
    /// you write; for a generated context that cannot be edited, use <see cref="UseIyuBoardTables"/>.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The same builder.</returns>
    public static ModelBuilder ApplyIyuBoardTables(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<IyuBoardRow>(board =>
        {
            board.ToTable("IyuBoards");
            board.HasKey(b => b.Id);
            board.Property(b => b.Id).HasMaxLength(IyuBoardService.MaxTextLength);
            board.Property(b => b.Name).HasMaxLength(IyuBoardService.MaxTextLength);
        });
        modelBuilder.Entity<IyuBoardRevisionRow>(revision =>
        {
            revision.ToTable("IyuBoardRevisions");
            revision.HasKey(r => new { r.BoardId, r.Revision });
            revision.Property(r => r.BoardId).HasMaxLength(IyuBoardService.MaxTextLength);
            revision.Property(r => r.CreatedBy).HasMaxLength(IyuBoardService.MaxTextLength);
        });
        modelBuilder.Entity<IyuBoardPublicationRow>(publication =>
        {
            publication.ToTable("IyuBoardPublications");
            publication.HasKey(p => new { p.BoardId, p.Sequence });
            publication.Property(p => p.BoardId).HasMaxLength(IyuBoardService.MaxTextLength);
            publication.Property(p => p.PublishedBy).HasMaxLength(IyuBoardService.MaxTextLength);
        });
        return modelBuilder;
    }

    /// <summary>
    /// Adds the tables of <see cref="ApplyIyuBoardTables"/> to a context's model without touching the context's source: for a host
    /// whose context is generated and not edited by hand. The context's own model is built as usual and the tables are added to it.
    /// </summary>
    /// <param name="options">The options of the context.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// This replaces the context's model customizer, so a host that already replaces it with its own does not combine the two; such a
    /// host calls <see cref="ApplyIyuBoardTables"/> from its own customizer instead.
    /// </remarks>
    public static DbContextOptionsBuilder UseIyuBoardTables(this DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ReplaceService<IModelCustomizer, IyuBoardModelCustomizer>();
    }

    /// <summary>The typed form of <see cref="UseIyuBoardTables(DbContextOptionsBuilder)"/>.</summary>
    /// <typeparam name="TContext">The context.</typeparam>
    /// <param name="options">The options of the context.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder<TContext> UseIyuBoardTables<TContext>(this DbContextOptionsBuilder<TContext> options)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)options).UseIyuBoardTables();
        return options;
    }

    /// <summary>
    /// Turns on <see cref="IyuUBoardOptions.Boards"/> over the tables the library ships, in the application's own context. The model
    /// has to contain them: the context calls <see cref="ApplyIyuBoardTables"/>, or its options call <see cref="UseIyuBoardTables(DbContextOptionsBuilder)"/>.
    /// </summary>
    /// <typeparam name="TContext">The application's <c>DbContext</c>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Names any further parts; the board store is always on.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddIyuUBoard<TContext>(this IServiceCollection services, Action<IyuUBoardOptions>? configure = null)
        where TContext : DbContext =>
        services.AddIyuUBoard<TContext, IyuBoardRow, IyuBoardRevisionRow, IyuBoardPublicationRow>(configure);
}

/// <summary>Builds the context's model as it would be and then adds the board tables to it.</summary>
internal sealed class IyuBoardModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.ApplyIyuBoardTables();
    }
}
