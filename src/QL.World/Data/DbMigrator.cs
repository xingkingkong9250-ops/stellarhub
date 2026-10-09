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
    private const int CURRENT_VERSION = 9;

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

                // U3-A: v5 adds images.color_family — backfill from existing
                // dominant_colors JSON in the same transaction (pure HSV math).
                if (target == 5)
                    BackfillColorFamilies(connection, transaction);

                // M11-A: v9 adds the library-managed storage columns
                // (library_hash / library_path / original_path /
                // entry_code) and the CAS + entry-code-counter tables.
                // Backfill library_hash ← file_hash and original_path ←
                // file_path so every pre-M11 row points at the software
                // library without a costly copy here; the M11-E wizard
                // fills library_path / entry_code and moves bytes.
                if (target == 9)
                    BackfillLibraryHash(connection, transaction);

                command.CommandText = $"PRAGMA user_version = {target};";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>
    /// Derives color_family from the first dominant color for rows that already
    /// have dominant_colors. Rows without colors (or with corrupt JSON) keep a
    /// NULL family — the color filter simply never matches them.
    /// </summary>
    private static void BackfillColorFamilies(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(long Id, string Json)>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id, dominant_colors FROM images
                WHERE color_family IS NULL
                  AND dominant_colors IS NOT NULL AND dominant_colors != '';
                """;
            using var reader = query.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach ((long id, string json) in rows)
        {
            string? family;
            try
            {
                var colors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                family = colors is { Count: > 0 }
                    ? Services.Colors.ColorFamily.HexToFamily(colors[0])
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                continue; // corrupt JSON must never abort the migration
            }
            if (family is null)
                continue;

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE images SET color_family = $family WHERE id = $id;";
            update.Parameters.AddWithValue("$family", family);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Fills library_hash ← file_hash and original_path ← file_path for
    /// pre-M11 rows. Physical files are not copied here — the M11-E
    /// migration wizard handles that (source=local rows are copied +
    /// hardlinked, source=remote rows are skipped + logged per the NAS
    /// bypass constraint). library_path and entry_code stay NULL until
    /// the wizard runs. A single UPDATE is atomic and safe; file_hash is
    /// NOT NULL in the v1 schema so every row qualifies.
    /// </summary>
    private static void BackfillLibraryHash(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE images
            SET library_hash = file_hash,
                original_path = file_path
            WHERE library_hash IS NULL;
            """;
        update.ExecuteNonQuery();
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
