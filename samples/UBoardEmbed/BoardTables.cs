using Iyu.UBoard.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace UBoardEmbed;

// The three tables are the application's own: the library states the columns it needs and nothing else.
public class BoardRow : IBoardRow
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Document { get; set; } = "";

    public int CurrentRevision { get; set; }

    public int? PublishedRevision { get; set; }
}

public class BoardRevisionRow : IBoardRevisionRow
{
    public string BoardId { get; set; } = "";

    public int Revision { get; set; }

    public string Document { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";
}

public class BoardPublicationRow : IBoardPublicationRow
{
    public string BoardId { get; set; } = "";

    public int Sequence { get; set; }

    public int? Revision { get; set; }

    public DateTimeOffset PublishedAt { get; set; }

    public string PublishedBy { get; set; } = "";
}

public class BoardsDb(DbContextOptions<BoardsDb> options) : DbContext(options)
{
    public DbSet<BoardRow> Boards => Set<BoardRow>();

    public DbSet<BoardRevisionRow> Revisions => Set<BoardRevisionRow>();

    public DbSet<BoardPublicationRow> Publications => Set<BoardPublicationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoardRow>().HasKey(b => b.Id);
        modelBuilder.Entity<BoardRevisionRow>().HasKey(r => new { r.BoardId, r.Revision });
        modelBuilder.Entity<BoardPublicationRow>().HasKey(p => new { p.BoardId, p.Sequence });
    }
}
