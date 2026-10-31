using Lowline;

namespace Iyu.Lowline;

/// <summary>
/// Opt-in: the records an application already holds when it starts using Lowline, which the engine should learn from so
/// that the first suggestions do not wait for people to settle enough new ones. One implementation per form, registered
/// with the container (scoped) next to that form's <see cref="ILowlineFormHook"/>. A form with no implementation learns
/// from settled records only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implementing this is a declaration:</b> every record this source returns was typed by a person with no suggestion in
/// front of them. The engine counts a record as evidence only on that footing — a record handed over as the application's
/// own doing (<see cref="SettledBy.Application"/>) is stored but neither counted nor allowed to lower the threshold for a
/// suggestion, so history handed over that way would grow the store without ever producing one. Whether a given row was
/// typed by a person is something only the application knows (an import, a bulk fill or a value a rule wrote is not), which
/// is why the host asks instead of guessing. Return only the rows for which it is true — typically the rows from before
/// the installation, filtered by the date it began.
/// </para>
/// <para>
/// Each record is learned once, with no draft and no settler, and the history pass skips an id the store already holds.
/// A person who later settles the same record through <see cref="IyuLowlineSettleService"/> replaces what the pass learned.
/// The ids must also be among <see cref="ILowlineFormHook.ListSourceKeysAsync"/> — the recovery pass forgets a record whose
/// id is not in that set, and would forget the history it was just given.
/// </para>
/// </remarks>
public interface ILowlineHistorySource
{
    /// <summary>The name of the form this source serves — the same as its <see cref="ILowlineFormHook.Form"/>.</summary>
    string Form { get; }

    /// <summary>
    /// Every memory space that has history to learn from (a workplace, a team). The store has no scope for a space until a
    /// record is learned in it, so on the first day only the application can say which spaces to look at.
    /// </summary>
    Task<IReadOnlyList<string>> ListHistorySpacesAsync(CancellationToken cancellationToken);

    /// <summary>The ids of every history record in <paramref name="space"/>, without reading the records.</summary>
    Task<IReadOnlySet<string>> ListHistoryKeysAsync(string space, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the history records with these ids as stored. An id with no row any more is left out of the answer; so is a
    /// row the application will not stand behind.
    /// </summary>
    Task<IReadOnlyList<LowlineHistoryRecord>> ReadHistoryAsync(
        string space, IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken);
}

/// <summary>A history record, as the engine needs it.</summary>
/// <param name="Id">The record's id — the same id <see cref="ILowlineFormHook.ReadSavedAsync"/> and the source listing use.</param>
/// <param name="Values">The record's values by field name, as stored.</param>
public sealed record LowlineHistoryRecord(string Id, IReadOnlyDictionary<string, FieldValue> Values);
