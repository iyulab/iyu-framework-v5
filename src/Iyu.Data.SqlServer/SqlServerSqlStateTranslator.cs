using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Iyu.Data.SqlServer;

/// <summary>
/// Names the standard SQLSTATE of a SQL Server error. SQL Server's client reports the server's own error number
/// (<see cref="SqlException.Number"/>) and no SQLSTATE, so a write failure there would otherwise be unclassified.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>2627, 2601</term><description><c>23505</c> — a duplicate in a unique constraint or index</description></item>
/// <item><term>547</term><description><c>23503</c> for a foreign key, <c>23514</c> for a check constraint — the server
/// reports both under one number, and the kind of constraint is named only in the message</description></item>
/// <item><term>515</term><description><c>23502</c> — a missing required value</description></item>
/// <item><term>2628, 8152</term><description><c>22001</c> — a value too long for its column</description></item>
/// <item><term>1205</term><description><c>40001</c> — chosen as a deadlock victim</description></item>
/// </list>
/// Error 547 is told apart by the constraint kind the message names (<c>FOREIGN KEY</c>, <c>REFERENCE</c>,
/// <c>CHECK</c>) — SQL Server's English text. A server whose messages are in another language leaves 547 untranslated,
/// and the failure stays the neutral conflict rather than a guess.
/// </remarks>
public sealed class SqlServerSqlStateTranslator : ISqlStateTranslator
{
    public string? SqlStateOf(DbException exception) => exception is SqlException sql ? SqlStateOf(sql.Number, sql.Message) : null;

    /// <summary>The SQLSTATE for a SQL Server error <paramref name="number"/> with <paramref name="message"/>.</summary>
    public static string? SqlStateOf(int number, string message) => number switch
    {
        2627 or 2601 => "23505",
        547 when message.Contains("FOREIGN KEY", StringComparison.Ordinal)
                 || message.Contains("REFERENCE constraint", StringComparison.Ordinal) => "23503",
        547 when message.Contains("CHECK constraint", StringComparison.Ordinal) => "23514",
        515 => "23502",
        2628 or 8152 => "22001",
        1205 => "40001",
        _ => null,
    };
}
