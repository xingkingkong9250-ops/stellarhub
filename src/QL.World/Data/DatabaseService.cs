using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Services;

namespace QL.World.Data;

/// <summary>
/// Owns the single long-lived SQLite connection (WAL) and serializes
/// access to it. SQLite connections are not thread-safe; all callers go
/// through <see cref="Run"/> / <see cref="RunInTransaction"/> which take
/// an internal lock. SQL must always be parameterized.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private bool _initialized;

    public DatabaseService(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Directory.CreateDirectory(dataRoot);
        string databasePath = Path.Combine(dataRoot, AppDataRootResolver.DatabaseFileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    /// <summary>Opens the connection, applies pragmas and migrations.</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
                return;

            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            ExecutePragma("PRAGMA journal_mode = WAL;");
            ExecutePragma("PRAGMA foreign_keys = ON;");
            ExecutePragma("PRAGMA busy_timeout = 5000;");
            ExecutePragma("PRAGMA synchronous = NORMAL;");

            DbMigrator.Migrate(_connection);
            _initialized = true;
        }
    }

    /// <summary>Runs a serialized read/write action against the connection.</summary>
    public void Run(Action<SqliteConnection> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            EnsureInitialized();
            action(_connection!);
        }
    }

    /// <summary>Runs a serialized read/write function against the connection.</summary>
    public T Run<T>(Func<SqliteConnection, T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        lock (_gate)
        {
            EnsureInitialized();
            return func(_connection!);
        }
    }

    /// <summary>
    /// Runs a batch inside one transaction (50-row import batches etc.).
    /// The callback receives connection + transaction and must attach
    /// both to every command.
    /// </summary>
    public T RunInTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        lock (_gate)
        {
            EnsureInitialized();
            using var transaction = _connection!.BeginTransaction();
            try
            {
                T result = func(_connection, transaction);
                transaction.Commit();
                return result;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public void RunInTransaction(Action<SqliteConnection, SqliteTransaction> action) =>
        RunInTransaction((conn, tx) =>
        {
            action(conn, tx);
            return true;
        });

    /// <summary>Quick integrity gate for W3/A1 startup check.</summary>
    public string QuickIntegrityCheck() =>
        Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            return Convert.ToString(command.ExecuteScalar()) ?? "unknown";
        });

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
            _initialized = false;
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _connection is null)
            throw new InvalidOperationException(
                "DatabaseService.Initialize() must be called before use.");
    }

    private void ExecutePragma(string sql)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
