using System.Data.Common;

namespace Iyu.Data;

/// <summary>
/// Names SQLite's <c>no such table</c> by its standard SQLSTATE, <c>42S02</c> (base table or view not found). SQLite reports
/// no SQLSTATE and files this under its generic error code, so only its message tells it from any other failure.
/// </summary>
/// <remarks>
/// Recognised by the exception's type name rather than the type, so this framework references no SQLite package; a failure
/// of any other kind — or of any other provider — is left unnamed. It is registered by <c>AddIyuMainServer</c>.
/// </remarks>
public sealed class SqliteSqlStateTranslator : ISqlStateTranslator
{
    private const string ExceptionTypeName = "Microsoft.Data.Sqlite.SqliteException";

    public string? SqlStateOf(DbException exception)
        => exception.GetType().FullName == ExceptionTypeName
           && exception.Message.Contains("no such table: ", StringComparison.Ordinal)
            ? "42S02"
            : null;
}
