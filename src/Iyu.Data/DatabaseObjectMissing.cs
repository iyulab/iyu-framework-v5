using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Iyu.Data;

/// <summary>
/// A data store failure that says the database does not have a table or view the host maps — the schema was never applied to
/// it, or not the one this host was built for.
/// </summary>
/// <remarks>
/// <para>
/// Recognised by the standard SQLSTATE the provider reports through ADO.NET's <see cref="DbException.SqlState"/> —
/// <c>42P01</c> (PostgreSQL's undefined table) and <c>42S02</c> (the standard class for a base table or view not found) —
/// or, for a provider that reports none, as a registered <see cref="ISqlStateTranslator"/> names it (SQL Server's error
/// <c>208</c> and SQLite's <c>no such table</c> are). Nothing else is recognised: a failure that is not this one is not
/// touched.
/// </para>
/// <para>
/// <see cref="ObjectName"/> is read from the provider's message when it carries one in a shape this knows
/// (PostgreSQL's <c>relation "x" does not exist</c>, SQL Server's <c>Invalid object name 'x'</c>, SQLite's
/// <c>no such table: x</c>); otherwise it is <see langword="null"/>. Only the name is taken from the message — the
/// message itself is for the server's log, not for a caller.
/// </para>
/// </remarks>
public sealed class DatabaseObjectMissing
{
    /// <summary>The <c>code</c> a response carries for this failure, on every surface that answers it.</summary>
    public const string Code = "DatabaseObjectMissing";

    private static readonly string[] SqlStates = ["42P01", "42S02"];

    private static readonly Regex[] NameShapes =
    [
        new("relation \"(?<name>[^\"]+)\" does not exist", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        new("Invalid object name '(?<name>[^']+)'", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        new("no such table: (?<name>[^\\s'\"]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
    ];

    private static readonly object ReportedKey = new();

    private DatabaseObjectMissing(DbException failure, string? objectName)
    {
        Failure = failure;
        ObjectName = objectName;
    }

    /// <summary>The provider's own exception — what the server's log carries.</summary>
    public DbException Failure { get; }

    /// <summary>The table or view the database lacks, as the provider names it, or <see langword="null"/> when its message does not say.</summary>
    public string? ObjectName { get; }

    /// <summary>
    /// The sentence a caller is told where details are wanted: the fact first, then what to do about it. Withhold it
    /// elsewhere — what a host maps is a fact about how it is built.
    /// </summary>
    public string Detail => (ObjectName is null
        ? "The database has no table or view that this host maps."
        : $"The database has no table or view named '{ObjectName}', which this host maps.")
        + " The schema has not been applied to it. Apply the schema to the database, then retry.";

    /// <summary>
    /// Whether <paramref name="exception"/>, or an exception it wraps, is a provider failure that says the database lacks a
    /// table or view.
    /// </summary>
    /// <param name="exception">What was thrown — the provider's own exception or any exception that wraps it.</param>
    /// <param name="translators">The registered translators, for a provider that reports no SQLSTATE.</param>
    public static DatabaseObjectMissing? Find(Exception exception, IEnumerable<ISqlStateTranslator> translators)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(translators);

        var registered = translators as ISqlStateTranslator[] ?? translators.ToArray();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not DbException db) continue;
            var state = db.SqlState is { Length: > 0 } reported ? reported : Translated(db, registered);
            if (state is not null && Array.IndexOf(SqlStates, state) >= 0)
                return new DatabaseObjectMissing(db, NameIn(db.Message));
        }
        return null;
    }

    private static string? Translated(DbException exception, ISqlStateTranslator[] translators)
    {
        foreach (var translator in translators)
            if (translator.SqlStateOf(exception) is { Length: > 0 } translated) return translated;
        return null;
    }

    private static string? NameIn(string message)
    {
        foreach (var shape in NameShapes)
            if (shape.Match(message) is { Success: true } match) return match.Groups["name"].Value;
        return null;
    }

    /// <summary>
    /// Writes the failure to the server's log at Error level — with the provider's exception and, where the message
    /// names it, the object — once for the request whose <paramref name="requestItems"/> are given, however many
    /// places in it failed. A failure answered without a request logs every time.
    /// </summary>
    /// <param name="logger">Where to write.</param>
    /// <param name="exception">The exception as thrown — the whole chain is in the log.</param>
    /// <param name="requestItems">The request's per-request store (<c>HttpContext.Items</c>), or <see langword="null"/>.</param>
    public void Report(ILogger logger, Exception exception, IDictionary<object, object?>? requestItems)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        if (requestItems is not null)
        {
            lock (requestItems)
            {
                if (!requestItems.TryAdd(ReportedKey, true)) return;
            }
        }
        logger.LogError(exception,
            "The database does not have the table or view {DatabaseObject} that this host maps; the schema has not been applied to it.",
            ObjectName ?? "(not named by the provider)");
    }
}
