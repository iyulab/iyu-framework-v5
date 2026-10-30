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
    /// Says where a record being written belongs and what its caller may pick, so the engine can propose for it — or
    /// <c>null</c> when <paramref name="recordId"/> names a record <paramref name="user"/> may not work on. The record may
    /// not be saved yet: <paramref name="recordId"/> is whatever id the form element uses for it.
    /// </summary>
    Task<LowlineFormContext?> OpenAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken);

    /// <summary>
    /// The keys of every record that exists in <paramref name="space"/> — the ids a settled record may carry. The recovery
    /// pass forgets the settled records whose id is not in this set, which is how a deleted source is forgotten whatever
    /// removed it. <c>null</c> (the default) means this form cannot list its sources, and its records are never forgotten
    /// that way. A set that comes back short makes the pass forget records that still have a source — see
    /// <see cref="IyuLowlineOptions.MaxForgetFraction"/>.
    /// </summary>
    Task<IReadOnlySet<string>?> ListSourceKeysAsync(string space, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>?>(null);

    /// <summary>
    /// Reads the saved record <paramref name="recordId"/> as the application stored it, or <c>null</c> when there is no
    /// such record <em>that <paramref name="user"/> may settle</em>. Answering <c>null</c> for a record the caller may not
    /// see is how the hook keeps one person from settling another's record; the caller cannot tell the two apart.
    /// </summary>
    Task<LowlineSavedRecord?> ReadSavedAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>Where a record being written belongs and what its caller may pick, decided by the application.</summary>
/// <param name="Space">Which records answer this request and learn from its saves — a workplace, a team. Decided from the
/// signed-in caller, never from anything the request says of itself.</param>
/// <param name="Allowed">The choices the caller may pick now, by field — their permissions and active filters; a value
/// outside them is never suggested. <c>null</c>, or a field left out, allows any value.</param>
public sealed record LowlineFormContext(
    string Space,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Allowed = null);

/// <summary>A saved record, as the engine needs it: the memory space it belongs to and its decided fields' values.</summary>
/// <param name="Space">Which records learn together — a workplace, a team, whatever the host keeps apart. Decided by the
/// application from the stored row, never by the caller.</param>
/// <param name="Values">The record's values by field name, as stored.</param>
public sealed record LowlineSavedRecord(string Space, IReadOnlyDictionary<string, FieldValue> Values);
