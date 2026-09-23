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
        Assert.Equal(3, version);

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
            Assert.Equal(3, migrated);

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

            // Re-running Initialize must stay at v3 (idempotent forward-only).
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
}
