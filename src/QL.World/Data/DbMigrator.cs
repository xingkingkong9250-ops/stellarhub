using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace QL.World.Data;

/// <summary>
/// Forward-only schema migration keyed on PRAGMA user_version.
/// Each migration is applied inside a transaction exactly once and
/// must be idempotent (IF NOT EXISTS).
/// </summary>
internal static class DbMigrator
{
    private const int CURRENT_VERSION = 2;

    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int version = ReadUserVersion(connection);
        if (version >= CURRENT_VERSION)
            return;

        for (int target = version + 1; target <= CURRENT_VERSION; target++)
        {
            string sql = LoadSchemaSql(target);
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();

                command.CommandText = $"PRAGMA user_version = {target};";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    internal static string LoadSchemaSql(int version)
    {
        Assembly assembly = typeof(DbMigrator).Assembly;
        string resourceName = $"QL.World.Data.Schema.v{version}.sql";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded schema resource missing: {resourceName}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
