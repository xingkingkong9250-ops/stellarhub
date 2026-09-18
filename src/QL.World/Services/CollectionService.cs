using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// CRUD operations for collections (favorites) and their image associations.
/// Mirrors the TagService pattern: single DatabaseService dependency,
/// transactional writes, parameterized SQL.
/// </summary>
public sealed class CollectionService
{
    private readonly DatabaseService _db;

    public CollectionService(DatabaseService db) => _db = db;

    /// <summary>Creates a new collection. Returns its id.</summary>
    public long Create(string name, string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string now = DbTime.Now();
        return _db.RunInTransaction((conn, tx) =>
        {
            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO collections (name, description, cover_image_id, created_at, updated_at)
                VALUES ($name, $desc, NULL, $now, $now);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$name", name.Trim());
            command.Parameters.AddWithValue("$desc", description);
            command.Parameters.AddWithValue("$now", now);
            return Convert.ToInt64(command.ExecuteScalar());
        });
    }

    /// <summary>Renames a collection. Throws on UNIQUE constraint violation.</summary>
    public void Rename(long id, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        string now = DbTime.Now();
        _db.RunInTransaction((conn, tx) =>
        {
            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                UPDATE collections SET name = $name, updated_at = $now
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", newName.Trim());
            command.Parameters.AddWithValue("$now", now);
            int affected = command.ExecuteNonQuery();
            if (affected == 0)
                throw new InvalidOperationException($"Collection {id} not found.");
        });
    }

    public void UpdateDescription(long id, string description)
    {
        string now = DbTime.Now();
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                UPDATE collections SET description = $desc, updated_at = $now
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$desc", description);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Deletes a collection. CASCADE removes collection_images rows.</summary>
    public void Delete(long id)
    {
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM collections WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Sets or clears (null) the cover image for a collection.</summary>
    public void SetCover(long id, long? imageId)
    {
        string now = DbTime.Now();
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                UPDATE collections SET cover_image_id = $cover, updated_at = $now
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddNullable("$cover", imageId);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Adds images to a collection. Idempotent via INSERT OR IGNORE.
    /// Sort order advances per newly added image.</summary>
    public void AddImages(long id, IReadOnlyCollection<long> imageIds)
    {
        if (imageIds.Count == 0) return;
        _db.RunInTransaction((conn, tx) =>
        {
            foreach (long imageId in imageIds)
            {
                int nextOrder = GetNextSortOrder(conn, tx, id);
                using var command = conn.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT OR IGNORE INTO collection_images (collection_id, image_id, sort_order)
                    VALUES ($cid, $iid, $order);
                    """;
                command.Parameters.AddWithValue("$cid", id);
                command.Parameters.AddWithValue("$iid", imageId);
                command.Parameters.AddWithValue("$order", nextOrder);
                command.ExecuteNonQuery();
            }
        });
    }

    /// <summary>Removes a single image from a collection.</summary>
    public void RemoveImage(long id, long imageId)
    {
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                DELETE FROM collection_images
                WHERE collection_id = $cid AND image_id = $iid;
                """;
            command.Parameters.AddWithValue("$cid", id);
            command.Parameters.AddWithValue("$iid", imageId);
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Reorders images within a collection in one transaction.</summary>
    public void Reorder(long id, IReadOnlyList<long> orderedImageIds)
    {
        _db.RunInTransaction((conn, tx) =>
        {
            for (int i = 0; i < orderedImageIds.Count; i++)
            {
                using var command = conn.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    UPDATE collection_images SET sort_order = $order
                    WHERE collection_id = $cid AND image_id = $iid;
                    """;
                command.Parameters.AddWithValue("$cid", id);
                command.Parameters.AddWithValue("$iid", orderedImageIds[i]);
                command.Parameters.AddWithValue("$order", i);
                command.ExecuteNonQuery();
            }
        });
    }

    /// <summary>Returns all collections with their image counts and cover hashes, ordered by name.</summary>
    public IReadOnlyList<CollectionCount> GetAll() =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT c.id, c.name, c.cover_image_id,
                       (SELECT COUNT(*) FROM collection_images ci
                        WHERE ci.collection_id = c.id) AS n,
                       (SELECT i.file_hash FROM images i
                        WHERE i.id = c.cover_image_id) AS cover_hash
                FROM collections c
                ORDER BY c.name COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<CollectionCount>();
            while (reader.Read())
            {
                result.Add(new CollectionCount(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(3),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return result;
        });

    /// <summary>Returns image ids in a collection ordered by sort_order.</summary>
    public IReadOnlyList<long> GetImageIds(long id) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT image_id FROM collection_images
                WHERE collection_id = $id
                ORDER BY sort_order;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            var result = new List<long>();
            while (reader.Read())
                result.Add(reader.GetInt64(0));
            return result;
        });

    public int GetImageCount(long id) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM collection_images WHERE collection_id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(command.ExecuteScalar());
        });

    /// <summary>Returns the file_hash of the cover image, or null if unset.</summary>
    public string? GetCoverHash(long id) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT i.file_hash
                FROM collections c
                JOIN images i ON i.id = c.cover_image_id
                WHERE c.id = $id AND c.cover_image_id IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? reader.GetString(0) : null;
        });

    private static int GetNextSortOrder(SqliteConnection conn, SqliteTransaction tx, long collectionId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT COALESCE(MAX(sort_order), -1) + 1
            FROM collection_images
            WHERE collection_id = $id;
            """;
        command.Parameters.AddWithValue("$id", collectionId);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
