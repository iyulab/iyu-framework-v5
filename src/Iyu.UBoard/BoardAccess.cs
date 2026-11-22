using System.Security.Claims;

namespace Iyu.UBoard;

/// <summary>What a person may do to a board. The application decides each; the layer asks.</summary>
public enum BoardAction
{
    /// <summary>Open the board, read its revisions and what it publishes, and ask the assistant about it. Without this the board does not exist for that person.</summary>
    View,

    /// <summary>Make a new board. Asked with the id the person chose.</summary>
    Create,

    /// <summary>Save revisions, rename — and let the assistant change the board.</summary>
    Edit,

    /// <summary>Publish a revision, roll a publication back, withdraw it.</summary>
    Publish,

    /// <summary>Delete the board with its whole history.</summary>
    Delete,
}

/// <summary>
/// Who may do what to which board. The application owns this: who created a board, which roles edit which boards, whether a
/// colleague may open someone else's — all of it lives in the application's own permission model, and this port is how the
/// layer asks. There is no access store and no sharing screen in the layer.
/// </summary>
/// <remarks>
/// <para>
/// Every board route asks this port, and the answer is enforced there — hiding a button is not enforcement. A person who may not
/// <see cref="BoardAction.View"/> a board is told it is not there (404), whatever else they asked; a person who may view but not
/// do the thing is told they may not (403). A list holds only the boards the person may view.
/// </para>
/// <para>
/// The layer fails closed: a host that turns the boards on without registering an implementation does not start. A host that
/// wants every signed-in person to do everything registers <see cref="EveryoneSignedInBoardAccess"/> by name.
/// </para>
/// </remarks>
public interface IBoardAccess
{
    /// <summary>Whether <paramref name="user"/> may do <paramref name="action"/> to the board <paramref name="boardId"/>.</summary>
    /// <param name="user">The signed-in caller.</param>
    /// <param name="action">What they want to do.</param>
    /// <param name="boardId">The board; for <see cref="BoardAction.Create"/>, the id they chose, or null when only the question "may this person make boards at all" is being asked.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    ValueTask<bool> AllowsAsync(ClaimsPrincipal user, BoardAction action, string? boardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A board was just made by <paramref name="user"/>. The application records who owns it and who else may edit it in its own
    /// permission model, so the next <see cref="AllowsAsync"/> answers for the new board. Called once, after the board exists.
    /// </summary>
    /// <param name="user">Who made it.</param>
    /// <param name="boardId">The new board.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every signed-in person may do everything to every board. A host registers it knowing that — it is the explicit way to say "no
/// board-level rules", for a sample or an app whose users are all trusted editors.
/// </summary>
public sealed class EveryoneSignedInBoardAccess : IBoardAccess
{
    /// <inheritdoc />
    public ValueTask<bool> AllowsAsync(ClaimsPrincipal user, BoardAction action, string? boardId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(user.Identity?.IsAuthenticated == true);

    /// <inheritdoc />
    public ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
