using System.Text.Json;

namespace Iyu.UBoard;

/// <summary>
/// What a host does with its boards: make one, save a revision of it, publish a revision, and find the last good publication
/// when the one in force turns out to be unusable. Registered by <c>AddIyuUBoard</c> with <c>Boards()</c>.
/// </summary>
/// <remarks>
/// The document is opaque here. The service checks only that it is JSON, because a store that kept text it could not
/// read back as JSON would break every viewer; whether it is a board a viewer accepts is decided where the board language
/// lives, and <see cref="FindLastAcceptedPublicationAsync"/> takes that decision as a function.
/// </remarks>
public sealed class IyuBoardService(IBoardStore store, TimeProvider time)
{
    /// <summary>The longest id or name a board may have; it is what a stored column is sized for.</summary>
    public const int MaxTextLength = 200;

    /// <summary>Makes a board whose first revision is <paramref name="document"/>.</summary>
    /// <param name="id">The board's id: not blank, no leading or trailing spaces, at most <see cref="MaxTextLength"/> characters.</param>
    /// <param name="name">The board's name: not blank, at most <see cref="MaxTextLength"/> characters.</param>
    /// <param name="document">The first document, as JSON.</param>
    /// <param name="user">Who makes it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">A value is blank, too long, or the document is not JSON.</exception>
    public Task<BoardChange> CreateAsync(string id, string name, string document, string user, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        CheckText(name, nameof(name));
        CheckDocument(document);
        CheckText(user, nameof(user));
        return store.CreateAsync(id, name, document, user, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>The boards without their documents, ordered by id, a page at a time.</summary>
    /// <param name="skip">How many boards to pass over.</param>
    /// <param name="take">How many boards to return at most: from 1 to 1000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skip"/> is negative or <paramref name="take"/> is outside 1 to 1000.</exception>
    public Task<IReadOnlyList<BoardSummary>> ListAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, 1000);
        return store.ListAsync(skip, take, cancellationToken);
    }

    /// <summary>Changes the board's name; its document and revision stay as they are.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="name">The new name: not blank, at most <see cref="MaxTextLength"/> characters.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">A value is blank or too long.</exception>
    public Task<BoardChange> RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        CheckText(name, nameof(name));
        return store.RenameAsync(id, name, cancellationToken);
    }

    /// <summary>
    /// Deletes the board with every revision and its whole publication history, as one step. The publication history is the record
    /// of who published what and when; a host that must keep it hides the board with a column of its own and does not call this.
    /// </summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>True when there was such a board.</returns>
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        return store.DeleteAsync(id, cancellationToken);
    }

    /// <summary>The board, or null when there is none.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<BoardRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        return store.FindAsync(id, cancellationToken);
    }

    /// <summary>
    /// Saves <paramref name="document"/> as the board's next revision, on the revision the caller worked from. A save made on a
    /// revision the board has moved past changes nothing and answers <see cref="BoardChangeStatus.Conflict"/> with the board as it
    /// stands, so the caller can reload and try again.
    /// </summary>
    /// <param name="id">The board's id.</param>
    /// <param name="document">The new document, as JSON.</param>
    /// <param name="expectedRevision">The revision the caller worked from; at least 1.</param>
    /// <param name="user">Who saves it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">A value is blank, too long, or the document is not JSON.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedRevision"/> is below 1.</exception>
    public Task<BoardChange> SaveAsync(string id, string document, int expectedRevision, string user, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        CheckDocument(document);
        CheckText(user, nameof(user));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return store.SaveAsync(id, document, expectedRevision, user, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>One revision of a board, or null when there is none.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="revision">The revision number.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<BoardRevisionRecord?> GetRevisionAsync(string id, int revision, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        return store.FindRevisionAsync(id, revision, cancellationToken);
    }

    /// <summary>The board's revisions, newest first.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<IReadOnlyList<BoardRevisionRecord>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        return store.ListRevisionsAsync(id, cancellationToken);
    }

    /// <summary>
    /// Publishes a revision of the board. Publishing an older revision is how a publication is rolled back: the history gains a
    /// line and the pointer moves, and no revision is touched.
    /// </summary>
    /// <param name="id">The board's id.</param>
    /// <param name="revision">The revision to publish.</param>
    /// <param name="user">Who publishes it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The line added to the history, or null when there is no such board or no such revision of it.</returns>
    public Task<BoardPublicationRecord?> PublishAsync(string id, int revision, string user, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        CheckText(user, nameof(user));
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        return store.PublishAsync(id, revision, user, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>Withdraws the board's publication: the history gains a line that names no revision.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="user">Who withdraws it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The line added to the history, or null when there is no such board.</returns>
    public Task<BoardPublicationRecord?> WithdrawAsync(string id, string user, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        CheckText(user, nameof(user));
        return store.PublishAsync(id, null, user, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>The board's publication history, newest first.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<IReadOnlyList<BoardPublicationRecord>> ListPublicationsAsync(string id, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        return store.ListPublicationsAsync(id, cancellationToken);
    }

    /// <summary>The revision the board is publishing, or null when it is unpublished or there is no such board.</summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<BoardRevisionRecord?> GetPublishedAsync(string id, CancellationToken cancellationToken = default)
    {
        CheckId(id);
        var board = await store.FindAsync(id, cancellationToken);
        return board?.PublishedRevision is { } revision
            ? await store.FindRevisionAsync(id, revision, cancellationToken)
            : null;
    }

    /// <summary>
    /// The revisions that were ever published before the one in force, newest publication first, each once, with their documents.
    /// This is what a reader falls back through when the publication in force is one its viewer refuses. A revision with a lower
    /// number is not necessarily one that was ever published (it may be a draft), so the list comes from the publication history.
    /// </summary>
    /// <param name="id">The board's id.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The revisions; empty when the board is not being published or nothing was published before the revision in force.</returns>
    public async Task<IReadOnlyList<BoardRevisionRecord>> ListEarlierPublicationsAsync(string id, CancellationToken cancellationToken = default)
    {
        CheckId(id);

        var board = await store.FindAsync(id, cancellationToken);
        if (board?.PublishedRevision is not { } inForce) return [];

        var earlier = new List<BoardRevisionRecord>();
        var seen = new HashSet<int> { inForce };
        foreach (var line in await store.ListPublicationsAsync(id, cancellationToken))
        {
            // A withdrawal names no revision, and a revision published again is listed once.
            if (line.Revision is not { } revision || !seen.Add(revision)) continue;

            if (await store.FindRevisionAsync(id, revision, cancellationToken) is { } found) earlier.Add(found);
        }

        return earlier;
    }

    /// <summary>
    /// For a board whose publication a viewer refused — the JSON was fine and the board language was not — finds the newest
    /// earlier publication (<see cref="ListEarlierPublicationsAsync"/>) that <paramref name="accepts"/>. For a host that can run
    /// the check itself; a host whose check runs in the browser sends the list to the reader instead.
    /// </summary>
    /// <param name="id">The board's id.</param>
    /// <param name="accepts">Whether a document is one the viewer takes. The store never judges that.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>That revision, or null when the board is not being published or no earlier publication is accepted.</returns>
    public async Task<BoardRevisionRecord?> FindLastAcceptedPublicationAsync(
        string id, Func<string, bool> accepts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepts);
        return (await ListEarlierPublicationsAsync(id, cancellationToken)).FirstOrDefault(found => accepts(found.Document));
    }

    private static void CheckId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id != id.Trim()) throw new ArgumentException("A board id has no leading or trailing spaces.", nameof(id));
        if (id.Length > MaxTextLength) throw new ArgumentException($"A board id is at most {MaxTextLength} characters.", nameof(id));
    }

    private static void CheckText(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > MaxTextLength) throw new ArgumentException($"The {parameter} is at most {MaxTextLength} characters.", parameter);
    }

    private static void CheckDocument(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        try
        {
            using var _ = JsonDocument.Parse(document);
        }
        catch (JsonException)
        {
            throw new ArgumentException("A board document is JSON; this text is not.", nameof(document));
        }
    }
}
