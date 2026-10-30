namespace Iyu.Core.Lowline;

/// <summary>
/// One settled record, as the application's own table holds it. The application declares the table in its own schema
/// and gives its entity this interface; the store of the <c>Iyu.Lowline</c> package reads and writes it
/// through the application's <c>DbContext</c>. The interface lives here, not there, so the assembly that holds the
/// entities can implement it without referencing the host (ASP.NET Core, the engine, EF Core).
/// </summary>
/// <remarks>
/// The table needs a unique key over <see cref="Form"/>, <see cref="Space"/> and <see cref="RecordId"/>: the engine
/// keeps one record per id within a scope and a second save of the same id replaces the first. The store does not
/// create the table or its mapping — that stays with the schema the application already runs.
/// </remarks>
public interface ILowlineRecordRow
{
    /// <summary>The form the record was settled for.</summary>
    string Form { get; set; }

    /// <summary>The memory space within the form — a workplace, a team, whatever the host keeps apart.</summary>
    string Space { get; set; }

    /// <summary>The record's id within the scope; a later save of the same id replaces the earlier.</summary>
    string RecordId { get; set; }

    /// <summary>When the record was settled; the engine learns records in this order.</summary>
    DateTimeOffset SettledAt { get; set; }

    /// <summary>The record as the engine gave it back — one line of JSON, stored and returned unchanged.</summary>
    string Json { get; set; }
}

/// <summary>
/// The thresholds the engine chose for one scope, as the application's own table holds them.
/// </summary>
/// <remarks>The table needs a unique key over <see cref="Form"/> and <see cref="Space"/>.</remarks>
public interface ILowlineTuningRow
{
    /// <summary>The form the thresholds belong to.</summary>
    string Form { get; set; }

    /// <summary>The memory space within the form.</summary>
    string Space { get; set; }

    /// <summary>The tuning as the engine gave it back — stored and returned unchanged.</summary>
    string Tuning { get; set; }
}
