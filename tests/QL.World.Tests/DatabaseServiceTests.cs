using Microsoft.Data.Sqlite;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class DatabaseServiceTests
{
    [Fact]
    public void Initialize_creates_all_schema_v1_tables()
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
        });
    }

    [Fact]
    public void Initialize_sets_user_version_to_1_and_is_idempotent()
    {
        using var library = new TestLibrary();

        int version = library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        });
        Assert.Equal(1, version);

        // Second Initialize must not throw or duplicate anything.
        library.Db.Initialize();
        Assert.Equal("ok", library.Db.QuickIntegrityCheck());
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
