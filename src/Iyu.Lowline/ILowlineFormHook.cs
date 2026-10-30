using System.Security.Claims;
using Lowline;

namespace Iyu.Lowline;

/// <summary>
/// What the application tells Lowline about one form: how to read a record it has already saved. One implementation per
/// form, registered with the container (scoped — it reads the application's own tables).
/// </summary>
/// <remarks>
/// This is the single place the application's domain meets the engine. Everything the engine learns from a record comes
/// through here, from the <em>stored</em> row — never from the request that asked for the record to be settled.
/// </remarks>
public interface ILowlineFormHook
{
    /// <summary>The name of the form this hook serves — the name it was given in <c>Engine.Form</c>.</summary>
    string Form { get; }

    /// <summary>
    /// Reads the saved record <paramref name="recordId"/> as the application stored it, or <c>null</c> when there is no
    /// such record <em>that <paramref name="user"/> may settle</em>. Answering <c>null</c> for a record the caller may not
    /// see is how the hook keeps one person from settling another's record; the caller cannot tell the two apart.
    /// </summary>
    Task<LowlineSavedRecord?> ReadSavedAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>A saved record, as the engine needs it: the memory space it belongs to and its decided fields' values.</summary>
/// <param name="Space">Which records learn together — a workplace, a team, whatever the host keeps apart. Decided by the
/// application from the stored row, never by the caller.</param>
/// <param name="Values">The record's values by field name, as stored.</param>
public sealed record LowlineSavedRecord(string Space, IReadOnlyDictionary<string, FieldValue> Values);
