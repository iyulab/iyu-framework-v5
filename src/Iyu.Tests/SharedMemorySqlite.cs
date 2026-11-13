using Microsoft.Data.Sqlite;

namespace Iyu.Tests;

/// <summary>
/// A named in-memory SQLite database that every <c>DbContext</c> reaches through <b>its own</b> connection.
/// </summary>
/// <remarks>
/// A single <c>:memory:</c> connection handed to several contexts is one connection used by all of them. When the
/// server runs two requests at once (a GraphQL batch that splits under load, two parallel requests) a context that
/// initialises the shared connection registers its functions while the other's statement is still active, and SQLite
/// refuses: <c>SQLite Error 5: unable to delete/modify user-function due to active statements</c>. A shared-cache
/// memory database lives as long as one connection to it stays open, so the keep-alive connection held here keeps
/// the data and each context opens, uses and closes its own.
/// </remarks>
public sealed class SharedMemorySqlite : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public SharedMemorySqlite()
    {
        ConnectionString = $"Data Source=file:iyu-test-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    /// <summary>Hand this to <c>UseSqlite</c>: each context gets a connection of its own to the same database.</summary>
    public string ConnectionString { get; }

    public void Dispose() => _keepAlive.Dispose();
}
