using System.Security.Claims;
using System.Text.Json;

namespace Iyu.UBoard;

/// <summary>
/// The assistant behind a board's chat: one turn of a conversation about the board that is open. The layer owns the route and
/// everything that is asked before a turn runs; the model, the key and the loop that talks to it belong to whoever implements this
/// port (an adapter package, or the application itself), so the layer depends on no model library.
/// </summary>
/// <remarks>
/// <para>
/// A turn is state-free. The caller sends the conversation so far and what it wants next — a new message, or the results of the tools
/// the last answer asked it to run — and gets back either the tools to run or the assistant's words. The tools run where the caller
/// is (a board edit is checked and saved by the screen, as the person), so one question may take several turns; the history an answer
/// returns is handed back unchanged with the next one, and the layer never reads it.
/// </para>
/// <para>
/// The layer does its part before the port is called and the port does not repeat it: the caller is signed in and on this site, may
/// view the board (a board they may not view is 404 and never reaches the port), the request is well formed and small, and the tools
/// handed over are only the ones the caller may use — a person who may view the board but not edit it is handed no tool that
/// declares it <see cref="BoardToolDeclaration.Edits"/>, so the model is never offered one. The port should still treat everything
/// the screen sends, and the board's own text, as untrusted input to a model.
/// </para>
/// <para>
/// The assistant is off until an implementation is registered: with none, the turn route is not mapped and
/// <c>GET {prefix}/access</c> says <c>assistant: false</c>.
/// </para>
/// </remarks>
public interface IBoardAssistant
{
    /// <summary>Runs one turn.</summary>
    /// <param name="turn">The turn, as the layer vetted it.</param>
    /// <param name="cancellationToken">Cancels the turn, for example when the person presses stop.</param>
    /// <returns>The tools the caller is to run, or the assistant's words.</returns>
    /// <exception cref="BoardAssistantException">The assistant could not answer. The layer answers the caller with the failure's kind, never its details.</exception>
    Task<BoardAssistantAnswer> TakeTurnAsync(BoardAssistantTurn turn, CancellationToken cancellationToken = default);
}

/// <summary>One turn as the port receives it. Exactly one of <see cref="Message"/> and <see cref="Results"/> is set.</summary>
/// <param name="User">The signed-in caller; the port may use the identity to reach a service as the person, never to widen what they may do.</param>
/// <param name="BoardId">The board the conversation is about.</param>
/// <param name="Board">The board as the store holds it now, or null when nothing has been saved under that id yet (a screen starts a new board's conversation before its first save).</param>
/// <param name="CanEdit">Whether the caller may change this board. False means the turn is answer-only and <paramref name="Tools"/> holds no editing tool.</param>
/// <param name="System">The instructions the screen sends with the turn, or null.</param>
/// <param name="History">The conversation so far, exactly as an earlier answer returned it; empty for a first turn. Its shape belongs to the port.</param>
/// <param name="Message">A new message from the person; null when this turn carries <paramref name="Results"/>.</param>
/// <param name="Results">What the tools the last answer asked for returned; null when this turn carries <paramref name="Message"/>.</param>
/// <param name="Final">With <paramref name="Results"/> only: the words the screen ended the turn with itself. The results are recorded with them and the model is not asked again.</param>
/// <param name="Tools">The tools the caller can run, named in plain words, already limited to what the caller may use.</param>
public sealed record BoardAssistantTurn(
    ClaimsPrincipal User,
    string BoardId,
    BoardRecord? Board,
    bool CanEdit,
    string? System,
    IReadOnlyList<JsonElement> History,
    string? Message,
    IReadOnlyList<BoardToolResult>? Results,
    string? Final,
    IReadOnlyList<BoardToolDeclaration> Tools);

/// <summary>A tool the caller can run and the assistant may ask for.</summary>
/// <param name="Name">The tool's name: letters, digits, underscore and hyphen, at most 64.</param>
/// <param name="Description">What it does, for the model.</param>
/// <param name="Schema">The JSON Schema of its arguments.</param>
/// <param name="Edits">
/// Whether running it changes the board. A screen says so for every tool that does; a declaration that does not say is taken to
/// edit, so a tool is withheld from a read-only caller unless the screen vouches that it only reads. The word is the caller's own —
/// it keeps the model from being offered an edit the person may not make, and the save is what refuses the edit itself.
/// </param>
public sealed record BoardToolDeclaration(string Name, string? Description, JsonElement Schema, bool Edits = true);

/// <summary>What a tool returned, for the call with the same id.</summary>
/// <param name="CallId">The id the assistant gave the call.</param>
/// <param name="Output">What the tool returned, as text.</param>
public sealed record BoardToolResult(string CallId, string Output);

/// <summary>A tool the assistant asks the caller to run.</summary>
/// <param name="Id">The call's id, handed back with its result.</param>
/// <param name="Name">The tool.</param>
/// <param name="Arguments">The arguments, as JSON text.</param>
public sealed record BoardToolCall(string Id, string Name, string Arguments);

/// <summary>What a turn answered.</summary>
/// <param name="Stop"><c>AwaitingHostTools</c> when <paramref name="ToolCalls"/> must be run and their results sent back; <c>Completed</c> when the assistant has said what it has to say. A port may name another reason (a step limit, a refusal) and the screen treats it as the end of the answer.</param>
/// <param name="Text">What the assistant said, if anything.</param>
/// <param name="ToolCalls">The tools to run; empty when the turn is over.</param>
/// <param name="History">The conversation including this turn, for the caller to hand back next time.</param>
/// <param name="Usage">What the turn cost in tokens, in whatever shape the port reports it, or null.</param>
public sealed record BoardAssistantAnswer(
    string Stop,
    string? Text,
    IReadOnlyList<BoardToolCall> ToolCalls,
    IReadOnlyList<JsonElement> History,
    JsonElement? Usage = null)
{
    /// <summary>The <see cref="Stop"/> of an answer that asks the caller to run tools.</summary>
    public const string AwaitingHostTools = nameof(AwaitingHostTools);

    /// <summary>The <see cref="Stop"/> of an answer that ends the turn.</summary>
    public const string Completed = nameof(Completed);
}

/// <summary>
/// The assistant could not answer a turn. The layer tells the caller <see cref="Kind"/> — a short word the screen has a sentence for —
/// and nothing else; the message and the inner exception stay in the host's log.
/// </summary>
public sealed class BoardAssistantException : Exception
{
    /// <summary>The kind of failure a port reports when its service says the caller is asking too often; the layer answers 429 for it.</summary>
    public const string RateLimit = "ratelimit";

    /// <summary>The kind of failure for a turn the port cannot read — a history that is not one it gave. The layer answers 400 for it.</summary>
    public const string BadRequest = "badrequest";

    /// <summary>The kind of failure for one the port cannot name.</summary>
    public const string Unknown = "unknown";

    /// <summary>Makes the exception.</summary>
    /// <param name="kind">A short lower-case word naming the failure, for example <c>ratelimit</c>, <c>timeout</c>, <c>network</c>, <c>contextlimit</c> or <c>guard</c>.</param>
    /// <param name="message">What went wrong, for the log.</param>
    /// <param name="innerException">The failure behind it.</param>
    public BoardAssistantException(string kind, string? message = null, Exception? innerException = null)
        : base(message ?? kind, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
    }

    /// <summary>The kind of failure.</summary>
    public string Kind { get; }
}
