namespace Iyu.UBoard;

/// <summary>
/// Where boards are kept: the boards, every revision of each, and the history of what was published. The application owns the
/// tables; this port is how the layer reads and writes them.
/// </summary>
/// <remarks>
/// An implementation keeps these facts, and the services in this package rely on them without re-checking:
/// a revision is never changed or removed; a save is accepted only on the board's current revision and is decided by the store
/// itself, so two saves made on the same revision never both succeed; a save writes the revision and moves the board's
/// current document and revision together or not at all; a board's published revision is the revision of the last line of its
/// publication history, and is null when that line withdraws the publication.
/// </remarks>
public interface IBoardStore
{
    /// <summary>Makes a board whose first revision is <paramref name="document"/>.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="name">The board's name.</param>
    /// <param name="document">The first document, as JSON.</param>
    /// <param name="createdBy">Who makes it.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="BoardChangeStatus.Done"/>, or <see cref="BoardChangeStatus.AlreadyExists"/> with the board that is there.</returns>
    Task<BoardChange> CreateAsync(
        string id, string name, string document, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The board, or null when there is none.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<BoardRecord?> FindAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Saves <paramref name="document"/> as the board's next revision, if the board is still at <paramref name="expectedRevision"/>.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="document">The new document, as JSON.</param>
    /// <param name="expectedRevision">The revision the caller worked from.</param>
    /// <param name="createdBy">Who saves it.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="BoardChangeStatus.Done"/>, <see cref="BoardChangeStatus.Conflict"/> when the board has moved past the revision, or <see cref="BoardChangeStatus.NotFound"/>.</returns>
    Task<BoardChange> SaveAsync(
        string id, string document, int expectedRevision, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>One revision of a board, or null when there is none.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="revision">The revision number.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<BoardRevisionRecord?> FindRevisionAsync(string id, int revision, CancellationToken cancellationToken = default);

    /// <summary>The board's revisions, newest first; empty when there is no such board.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<BoardRevisionRecord>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a line to the board's publication history and points the board's published revision at it.
    /// </summary>
    /// <param name="id">The board's id.</param>
    /// <param name="revision">The revision to publish; null withdraws the publication.</param>
    /// <param name="publishedBy">Who.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The line added, or null when there is no such board or no such revision of it.</returns>
    Task<BoardPublicationRecord?> PublishAsync(
        string id, int? revision, string publishedBy, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The board's publication history, newest first; empty when there is no such board.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<BoardPublicationRecord>> ListPublicationsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>The boards, without their documents, ordered by id.</summary>
    /// <param name="skip">How many boards to pass over; at least 0.</param>
    /// <param name="take">How many boards to return at most; at least 1.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<BoardSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Changes the board's name. The name is not part of the document, so no revision is made and the board's revision does not move.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="BoardChangeStatus.Done"/> with the board, or <see cref="BoardChangeStatus.NotFound"/>.</returns>
    Task<BoardChange> RenameAsync(string id, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the board together with all its revisions and its whole publication history, in one step: afterwards none of them is
    /// left, and if the step fails none of them is removed. A host that wants the history kept as a record does not call this and
    /// hides the board with a column of its own instead.
    /// </summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>True when there was such a board; false when there was none.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
