namespace Iyu.UBoard;

/// <summary>
/// A board as the store keeps it. The document is JSON that the store hands back exactly as it was given: what a board may
/// contain is the board language's business, not the store's.
/// </summary>
/// <param name="Id">The board's id, chosen by the host.</param>
/// <param name="Name">What a person calls the board.</param>
/// <param name="Document">The current document: the document of <paramref name="CurrentRevision"/>.</param>
/// <param name="CurrentRevision">The revision a save must name to be accepted. The first revision is 1.</param>
/// <param name="PublishedRevision">The revision being published, or null while the board is unpublished or its publication was withdrawn.</param>
public sealed record BoardRecord(string Id, string Name, string Document, int CurrentRevision, int? PublishedRevision);

/// <summary>A board as a list shows it: without its document, which can be large.</summary>
/// <param name="Id">The board's id.</param>
/// <param name="Name">What a person calls the board.</param>
/// <param name="CurrentRevision">The revision a save must name to be accepted.</param>
/// <param name="PublishedRevision">The revision being published, or null while the board is unpublished or its publication was withdrawn.</param>
/// <param name="CreatedBy">Who saved the board's first revision, or null when the store does not know.</param>
/// <param name="UpdatedAt">When the current revision was saved, or null when the store does not know.</param>
/// <param name="UpdatedBy">Who saved the current revision, or null when the store does not know.</param>
public sealed record BoardSummary(
    string Id, string Name, int CurrentRevision, int? PublishedRevision,
    string? CreatedBy = null, DateTimeOffset? UpdatedAt = null, string? UpdatedBy = null);

/// <summary>One revision of a board. Revisions are only ever added, never changed or removed — until the board itself is deleted.</summary>
/// <param name="BoardId">The board.</param>
/// <param name="Revision">The revision number, from 1.</param>
/// <param name="Document">The document as it was saved.</param>
/// <param name="CreatedAt">When it was saved.</param>
/// <param name="CreatedBy">Who saved it.</param>
public sealed record BoardRevisionRecord(string BoardId, int Revision, string Document, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>One line of a board's publication history. Lines are only ever added.</summary>
/// <param name="BoardId">The board.</param>
/// <param name="Sequence">The line's place in the history, from 1.</param>
/// <param name="Revision">The revision that was published; null when the line withdraws the publication.</param>
/// <param name="PublishedAt">When.</param>
/// <param name="PublishedBy">Who.</param>
public sealed record BoardPublicationRecord(string BoardId, int Sequence, int? Revision, DateTimeOffset PublishedAt, string PublishedBy);

/// <summary>How a change to a board ended.</summary>
public enum BoardChangeStatus
{
    /// <summary>The change was made.</summary>
    Done,

    /// <summary>There is no such board.</summary>
    NotFound,

    /// <summary>A board with that id already exists.</summary>
    AlreadyExists,

    /// <summary>The save named a revision the board has moved past; nothing was written.</summary>
    Conflict,
}

/// <summary>What a change to a board answered.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Board">
/// The board after a <see cref="BoardChangeStatus.Done"/> change, or as it stands now after a <see cref="BoardChangeStatus.Conflict"/>
/// or <see cref="BoardChangeStatus.AlreadyExists"/> one, so the caller sees the revision to retry on; null when the board is not found.
/// </param>
public sealed record BoardChange(BoardChangeStatus Status, BoardRecord? Board)
{
    /// <summary>Whether the change was made.</summary>
    public bool Succeeded => Status == BoardChangeStatus.Done;
}
