using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class DatabaseServiceTests
{
    [Fact]
    public void Initialize_creates_all_current_schema_tables()
    {
        using var library = new TestLibrary();

        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = command.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read())
                tables.Add(reader.GetString(0));

            Assert.Contains("images", tables);
            Assert.Contains("image_tags", tables);
            Assert.Contains("collections", tables);
            Assert.Contains("collection_images", tables);
            Assert.Contains("image_embeddings", tables);
            Assert.Contains("meta", tables);
            Assert.Contains("pbr_groups", tables);
            Assert.Contains("pbr_group_images", tables);
        });
    }

    [Fact]
    public void Initialize_sets_user_version_to_current_and_is_idempotent()
    {
        using var library = new TestLibrary();

        int version = library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        });
        Assert.Equal(6, version);

        // Second Initialize must not throw or duplicate anything.
        library.Db.Initialize();
        Assert.Equal("ok", library.Db.QuickIntegrityCheck());
    }

    [Fact]
    public void Migration_from_v1_preserves_data_and_adds_pbr_tables()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Build a v1 database by hand (schema + user_version + one row).
            string databasePath = Path.Combine(directory, "qlworld.db");
            using (var bootstrap = new SqliteConnection($"Data Source={databasePath}"))
            {
                bootstrap.Open();
                using (var schema = bootstrap.CreateCommand())
                {
                    schema.CommandText = DbMigrator.LoadSchemaSql(1);
                    schema.ExecuteNonQuery();
                }
                using (var version = bootstrap.CreateCommand())
                {
                    version.CommandText = "PRAGMA user_version = 1;";
                    version.ExecuteNonQuery();
                }
                using (var seed = bootstrap.CreateCommand())
                {
                    seed.CommandText = """
                        INSERT INTO images
                            (file_path, file_name, sortable_name, file_hash, file_size,
                             width, height, format, rating, notes, primary_category,
                             dominant_colors, is_missing, created_at, updated_at)
                        VALUES ('C:\old\T_A.png', 'T_A.png', 't_a.png', 'h1', 1,
                                4, 4, 'png', 0, '', NULL, NULL, 0,
                                '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                        """;
                    seed.ExecuteNonQuery();
                }
            }

            using var db = new DatabaseService(directory);
            db.Initialize();

            int migrated = db.Run(conn =>
            {
                using var command = conn.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt32(command.ExecuteScalar());
            });
            Assert.Equal(6, migrated);

            var (tableCount, imageCount) = db.Run(conn =>
            {
                using var tables = conn.CreateCommand();
                tables.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' "
                    + "AND name IN ('pbr_groups', 'pbr_group_images');";
                int t = Convert.ToInt32(tables.ExecuteScalar());

                using var images = conn.CreateCommand();
                images.CommandText = "SELECT COUNT(*) FROM images;";
                int i = Convert.ToInt32(images.ExecuteScalar());
                return (t, i);
            });
            Assert.Equal(2, tableCount);
            Assert.Equal(1, imageCount); // v1 data survived the migration

            // U2-D: v4 migration adds image_embeddings.source with DEFAULT 'local'.
            string sourceDefault = db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT dflt_value FROM pragma_table_info('image_embeddings') WHERE name = 'source';";
                object? val = cmd.ExecuteScalar();
                return val?.ToString() ?? string.Empty;
            });
            Assert.Equal("'local'", sourceDefault);

            // Re-running Initialize must stay at v5 (idempotent forward-only).
            db.Initialize();
            Assert.Equal("ok", db.QuickIntegrityCheck());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Migration_to_v5_backfills_color_family_from_dominant_colors()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Build a v4 database (v1 schema + migrations 2-4) with analyzed rows.
            string databasePath = Path.Combine(directory, "qlworld.db");
            using (var bootstrap = new SqliteConnection($"Data Source={databasePath}"))
            {
                bootstrap.Open();
                foreach (int v in new[] { 1, 2, 3, 4 })
                {
                    using var schema = bootstrap.CreateCommand();
                    schema.CommandText = DbMigrator.LoadSchemaSql(v);
                    schema.ExecuteNonQuery();
                }
                using (var versionCmd = bootstrap.CreateCommand())
                {
                    versionCmd.CommandText = "PRAGMA user_version = 4;";
                    versionCmd.ExecuteNonQuery();
                }
                using var seed = bootstrap.CreateCommand();
                seed.CommandText = """
                    INSERT INTO images
                        (file_path, file_name, sortable_name, file_hash, file_size,
                         width, height, format, rating, notes, primary_category,
                         dominant_colors, is_missing, created_at, updated_at)
                    VALUES
                        ('C:\old\red.png', 'red.png', 'red.png', 'h1', 1,
                         4, 4, 'png', 0, '', NULL, '["#E04030","#101010"]', 0,
                         '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z'),
                        ('C:\old\gray.png', 'gray.png', 'gray.png', 'h2', 1,
                         4, 4, 'png', 0, '', NULL, '["#808080"]', 0,
                         '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z'),
                        ('C:\old\none.png', 'none.png', 'none.png', 'h3', 1,
                         4, 4, 'png', 0, '', NULL, NULL, 0,
                         '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z'),
                        ('C:\old\corrupt.png', 'corrupt.png', 'corrupt.png', 'h4', 1,
                         4, 4, 'png', 0, '', NULL, 'not-json', 0,
                         '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                    """;
                seed.ExecuteNonQuery();
            }

            using var db = new DatabaseService(directory);
            db.Initialize();

            int version = db.Run(conn =>
            {
                using var command = conn.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt32(command.ExecuteScalar());
            });
            Assert.Equal(6, version);

            var families = db.Run(conn =>
            {
                using var command = conn.CreateCommand();
                command.CommandText = "SELECT file_name, color_family, dominant_colors FROM images ORDER BY file_name;";
                var rows = new List<(string Name, string? Family, string? Colors)>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
                return rows;
            });

            // 首色归族；dominant_colors 原文不动。
            Assert.Equal(("corrupt.png", null, "not-json"), families[0]);
            Assert.Equal(("gray.png", "gray", """["#808080"]"""), families[1]);
            Assert.Equal(("none.png", null, null), families[2]);
            Assert.Equal(("red.png", "red", """["#E04030","#101010"]"""), families[3]);

            // 幂等：再次 Initialize 不重复回填、不报错。
            db.Initialize();
            Assert.Equal("ok", db.QuickIntegrityCheck());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Foreign_keys_are_enabled_and_cascade_deletes_tags()
    {
        using var library = new TestLibrary();
        long imageId = library.InsertImage();

        library.Db.RunInTransaction((conn, tx) =>
        {
            using var tag = conn.CreateCommand();
            tag.Transaction = tx;
            tag.CommandText = """
                INSERT INTO image_tags (image_id, tag, source, created_at)
                VALUES ($id, 'metal', 'manual', '2026-01-01T00:00:00.000Z');
                """;
            tag.Parameters.AddWithValue("$id", imageId);
            tag.ExecuteNonQuery();

            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM images WHERE id = $id;";
            del.Parameters.AddWithValue("$id", imageId);
            del.ExecuteNonQuery();
        });

        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM image_tags;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        });
    }

    [Fact]
    public void Migration_to_v6_adds_source_and_nas_connection_id_columns()
    {
        // Build a v5 library (run all schemas 1..5) with one local row and
        // assert the v6 migration backfills source='local' default + adds
        // the nullable nas_connection_id column + both indexes.
        string directory = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string databasePath = Path.Combine(directory, "qlworld.db");
            using (var bootstrap = new SqliteConnection($"Data Source={databasePath}"))
            {
                bootstrap.Open();
                foreach (int v in new[] { 1, 2, 3, 4, 5 })
                {
                    using var schema = bootstrap.CreateCommand();
                    schema.CommandText = DbMigrator.LoadSchemaSql(v);
                    schema.ExecuteNonQuery();
                }
                using var versionCmd = bootstrap.CreateCommand();
                versionCmd.CommandText = "PRAGMA user_version = 5;";
                versionCmd.ExecuteNonQuery();

                using var seed = bootstrap.CreateCommand();
                seed.CommandText = """
                    INSERT INTO images
                        (file_path, file_name, sortable_name, file_hash, file_size,
                         width, height, format, rating, notes, primary_category,
                         dominant_colors, is_missing, created_at, updated_at)
                    VALUES ('C:\old\img.png', 'img.png', 'img.png', 'h1', 1,
                            4, 4, 'png', 0, '', NULL, NULL, 0,
                            '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                    """;
                seed.ExecuteNonQuery();
            }

            using var db = new DatabaseService(directory);
            db.Initialize();

            int version = db.Run(conn =>
            {
                using var command = conn.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt32(command.ExecuteScalar());
            });
            Assert.Equal(6, version);

            // source column exists with DEFAULT 'local' and backfilled the v5 row.
            string sourceDefault = db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT dflt_value FROM pragma_table_info('images') WHERE name = 'source';";
                return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
            });
            Assert.Equal("'local'", sourceDefault);

            var (source, nasId) = db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT source, nas_connection_id FROM images WHERE file_hash = 'h1';";
                using var reader = cmd.ExecuteReader();
                reader.Read();
                return (reader.GetString(0), reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1));
            });
            Assert.Equal("local", source);
            Assert.Null(nasId);

            // nas_connection_id is nullable with no FK; both indexes present.
            string nasDefault = db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT dflt_value FROM pragma_table_info('images') WHERE name = 'nas_connection_id';";
                return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
            });
            Assert.Equal(string.Empty, nasDefault); // NULL default → empty dflt_value

            int indexCount = db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'index' AND name IN ('idx_images_source', 'idx_images_nas_connection');
                    """;
                return Convert.ToInt32(cmd.ExecuteScalar());
            });
            Assert.Equal(2, indexCount);

            // Idempotent re-init stays at v6.
            db.Initialize();
            Assert.Equal("ok", db.QuickIntegrityCheck());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
