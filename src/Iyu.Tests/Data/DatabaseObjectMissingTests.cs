using System.Data.Common;
using Iyu.Data;
using Iyu.Data.SqlServer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iyu.Tests.Data;

/// <summary>
/// What counts as «the database does not have a table or view the host maps» — by the standard SQLSTATE a provider reports, or
/// by a translator for one that reports none — and nothing else. Exceptions are built the way each provider's arrive; no server
/// runs here.
/// </summary>
public class DatabaseObjectMissingTests
{
    /// <summary>An exception the way ADO.NET exposes a provider that reports the standard class (<see cref="DbException.SqlState"/>).</summary>
    private sealed class StateException(string? sqlState, string message) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
    }

    /// <summary>A provider that reports its own error number, as SQL Server's client does, and no SQLSTATE.</summary>
    private sealed class NumberedException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    /// <summary>SQL Server's translator, driven by a number the test supplies (the real exception cannot be built outside the client).</summary>
    private sealed class NumberedAsSqlServer : ISqlStateTranslator
    {
        public string? SqlStateOf(DbException exception)
            => exception is NumberedException n ? SqlServerSqlStateTranslator.SqlStateOf(n.Number, n.Message) : null;
    }

    private static readonly ISqlStateTranslator[] None = [];

    [Theory]
    [InlineData("42P01", "42P01: relation \"project_full_view\" does not exist\n\nPOSITION: 15", "project_full_view")] // PostgreSQL
    [InlineData("42S02", "Base table or view not found: 1146 Table 'db.things' doesn't exist", null)]                 // the standard class, a message in no known shape
    [InlineData("42P01", "relation \"app.things\" does not exist", "app.things")]
    public void A_provider_that_reports_the_standard_class_is_recognised(string sqlState, string message, string? name)
    {
        var found = DatabaseObjectMissing.Find(new StateException(sqlState, message), None);

        Assert.NotNull(found);
        Assert.Equal(name, found.ObjectName);
    }

    [Fact]
    public void SQL_Server_error_208_is_recognised_through_its_translator_and_names_the_object()
    {
        var failure = new NumberedException(208, "Invalid object name 'dbo.project_full_view'.");

        var found = DatabaseObjectMissing.Find(failure, [new NumberedAsSqlServer()]);

        Assert.NotNull(found);
        Assert.Equal("dbo.project_full_view", found.ObjectName);
        Assert.Same(failure, found.Failure);
    }

    [Fact]
    public void SQL_Server_error_208_is_named_by_the_translator_as_the_standard_class()
        => Assert.Equal("42S02", SqlServerSqlStateTranslator.SqlStateOf(208, "Invalid object name 'dbo.T'."));

    [Fact]
    public void Without_a_translator_a_provider_that_reports_no_class_is_not_recognised()
        => Assert.Null(DatabaseObjectMissing.Find(new NumberedException(208, "Invalid object name 'dbo.T'."), None));

    [Fact]
    public async Task SQLite_no_such_table_is_recognised_through_its_translator_and_names_the_table()
    {
        // The exception SQLite really raises for a table that was never created — not a hand-built imitation of its text.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select * from Things";
        var failure = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteReaderAsync());
        Assert.Null(failure.SqlState); // the reason a translator is needed at all

        var found = DatabaseObjectMissing.Find(failure, [new SqliteSqlStateTranslator()]);

        Assert.NotNull(found);
        Assert.Equal("Things", found.ObjectName);
        Assert.Null(DatabaseObjectMissing.Find(failure, None));
    }

    [Theory]
    [InlineData("SQLite Error 1: 'no such column: Nope'.")]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: Things.Name'.")]
    [InlineData("SQLite Error 1: 'near \"selct\": syntax error'.")]
    public void SQLite_failures_that_are_not_a_missing_table_are_not_named(string message)
        => Assert.Null(new SqliteSqlStateTranslator().SqlStateOf(new SqliteException(message, 1)));

    [Fact]
    public void Another_providers_exception_with_the_same_words_is_not_taken_for_SQLite()
        => Assert.Null(new SqliteSqlStateTranslator().SqlStateOf(new StateException(null, "no such table: Things")));

    [Theory]
    [InlineData("23505")] // a unique violation
    [InlineData("42703")] // an undefined column — the schema is not the one the host expects, but this names tables and views
    [InlineData("40001")]
    [InlineData(null)]
    public void Any_other_failure_is_left_alone(string? sqlState)
        => Assert.Null(DatabaseObjectMissing.Find(new StateException(sqlState, "relation \"x\" does not exist"), None));

    [Fact]
    public void An_exception_that_is_no_provider_failure_is_left_alone()
    {
        Assert.Null(DatabaseObjectMissing.Find(new InvalidOperationException("boom"), None));
        Assert.Null(DatabaseObjectMissing.Find(new InvalidOperationException("outer", new InvalidOperationException("inner")), None));
    }

    [Fact]
    public void A_failure_a_write_or_a_query_wrapped_is_found_through_the_wrapper()
    {
        var provider = new StateException("42P01", "relation \"things\" does not exist");

        Assert.NotNull(DatabaseObjectMissing.Find(new DbUpdateException("saving", provider), None));
        Assert.NotNull(DatabaseObjectMissing.Find(new InvalidOperationException("a", new InvalidOperationException("b", provider)), None));
    }

    [Fact]
    public void The_sentence_for_a_caller_says_the_fact_first_and_the_remedy_second()
    {
        var named = DatabaseObjectMissing.Find(new StateException("42P01", "relation \"things\" does not exist"), None)!;
        var unnamed = DatabaseObjectMissing.Find(new StateException("42P01", "no shape"), None)!;

        Assert.StartsWith("The database has no table or view named 'things'", named.Detail, StringComparison.Ordinal);
        Assert.EndsWith("Apply the schema to the database, then retry.", named.Detail, StringComparison.Ordinal);
        Assert.StartsWith("The database has no table or view that this host maps.", unnamed.Detail, StringComparison.Ordinal);
    }

    private sealed class Sink : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public void The_log_gets_the_provider_exception_at_error_level_once_for_a_request()
    {
        var failure = new StateException("42P01", "relation \"things\" does not exist");
        var found = DatabaseObjectMissing.Find(failure, None)!;
        var sink = new Sink();
        var request = new Dictionary<object, object?>();

        found.Report(sink, failure, request);
        found.Report(sink, failure, request);

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains("things", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_outside_a_request_is_logged_each_time()
    {
        var failure = new StateException("42P01", "relation \"things\" does not exist");
        var found = DatabaseObjectMissing.Find(failure, None)!;
        var sink = new Sink();

        found.Report(sink, failure, null);
        found.Report(sink, failure, null);

        Assert.Equal(2, sink.Entries.Count);
    }
}
