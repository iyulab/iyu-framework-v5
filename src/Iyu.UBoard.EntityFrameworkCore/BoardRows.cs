namespace Iyu.UBoard.EntityFrameworkCore;

/// <summary>
/// One board, as the application's own table holds it. The table needs a key on <see cref="Id"/>. The document column holds JSON
/// text and is sized by the application.
/// </summary>
public interface IBoardRow
{
    /// <summary>The board's id; the table's key.</summary>
    string Id { get; set; }

    /// <summary>What a person calls the board.</summary>
    string Name { get; set; }

    /// <summary>The current document: the document of <see cref="CurrentRevision"/>. A read shortcut — the revision row is the record.</summary>
    string Document { get; set; }

    /// <summary>The revision a save must name to be accepted.</summary>
    int CurrentRevision { get; set; }

    /// <summary>The revision being published; null while unpublished or withdrawn. A read shortcut for the last line of the publication history.</summary>
    int? PublishedRevision { get; set; }
}

/// <summary>
/// One revision of a board, as the application's own table holds it. The table needs a composite key over
/// (<see cref="BoardId"/>, <see cref="Revision"/>), in that order: the key is what makes two saves on one revision unable to both succeed.
/// </summary>
public interface IBoardRevisionRow
{
    /// <summary>The board.</summary>
    string BoardId { get; set; }

    /// <summary>The revision number, from 1.</summary>
    int Revision { get; set; }

    /// <summary>The document as it was saved.</summary>
    string Document { get; set; }

    /// <summary>When it was saved.</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who saved it.</summary>
    string CreatedBy { get; set; }
}

/// <summary>
/// One line of a board's publication history, as the application's own table holds it. The table needs a composite key over
/// (<see cref="BoardId"/>, <see cref="Sequence"/>), in that order.
/// </summary>
public interface IBoardPublicationRow
{
    /// <summary>The board.</summary>
    string BoardId { get; set; }

    /// <summary>The line's place in the history, from 1.</summary>
    int Sequence { get; set; }

    /// <summary>The revision that was published; null when the line withdraws the publication.</summary>
    int? Revision { get; set; }

    /// <summary>When.</summary>
    DateTimeOffset PublishedAt { get; set; }

    /// <summary>Who.</summary>
    string PublishedBy { get; set; }
}
