using System.IO;
using Microsoft.Data.Sqlite;

namespace QL.World.Tests.Support;

/// <summary>
/// Builds a throwaway SQLite database that mimics the Python prototype's
/// schema (core/database.py) so LegacyPythonImporter can be tested offline.
/// </summary>
internal sealed class LegacyPythonDbBuilder : IDisposable
{
    private readonly string _path;

    public LegacyPythonDbBuilder()
    {
        _path = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            $"legacy_{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT UNIQUE NOT NULL,
                file_name TEXT NOT NULL,
                file_hash TEXT NOT NULL,
                file_size INTEGER,
                width INTEGER,
                height INTEGER,
                format TEXT,
                rating INTEGER DEFAULT 0,
                notes TEXT DEFAULT '',
                primary_category TEXT DEFAULT '',
                dominant_colors TEXT DEFAULT '[]',
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                imported_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE image_tags (
                image_id INTEGER REFERENCES images(id) ON DELETE CASCADE,
                tag TEXT NOT NULL,
                source TEXT DEFAULT 'ai',
                confidence REAL DEFAULT 1.0,
                PRIMARY KEY (image_id, tag)
            );
            CREATE TABLE collections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT UNIQUE NOT NULL,
                description TEXT DEFAULT '',
                cover_image_id INTEGER REFERENCES images(id),
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE collection_images (
                collection_id INTEGER REFERENCES collections(id) ON DELETE CASCADE,
                image_id INTEGER REFERENCES images(id) ON DELETE CASCADE,
                sort_order INTEGER DEFAULT 0,
                PRIMARY KEY (collection_id, image_id)
            );
            CREATE TABLE image_embeddings (
                image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                model_id TEXT,
                dim INTEGER,
                embedding BLOB
            );
            """;
        command.ExecuteNonQuery();
    }

    public string DbPath => _path;

    /// <summary>Inserts one image row the way the Python prototype would.</summary>
    public LegacyPythonDbBuilder AddImage(
        string filePath,
        string hash,
        string? category = "材质参考",
        string rating = "0",
        string? notes = null,
        string createdAt = "2025-06-01 08:30:00")
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO images (file_path, file_name, file_hash, file_size, width, height,
                                format, rating, notes, primary_category, created_at)
            VALUES ($path, $name, $hash, 2048, 100, 100, 'png', $rating, $notes, $category, $created);
            """;
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$name", System.IO.Path.GetFileName(filePath));
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$notes", notes ?? string.Empty);
        command.Parameters.AddWithValue("$category", (object?)category ?? string.Empty);
        command.Parameters.AddWithValue("$created", createdAt);
        command.ExecuteNonQuery();
        return this;
    }

    public LegacyPythonDbBuilder AddTag(long legacyImageId, string tag, string source = "ai")
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO image_tags (image_id, tag, source) VALUES ($id, $tag, $source);";
        command.Parameters.AddWithValue("$id", legacyImageId);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$source", source);
        command.ExecuteNonQuery();
        return this;
    }

    public LegacyPythonDbBuilder AddCollection(
        string name, long? legacyCoverImageId = null, string description = "")
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO collections (name, description, cover_image_id)
            VALUES ($name, $desc, $cover);
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$desc", description);
        command.Parameters.AddWithValue("$cover", (object?)legacyCoverImageId ?? DBNull.Value);
        command.ExecuteNonQuery();
        return this;
    }

    public LegacyPythonDbBuilder AddCollectionImage(string collectionName, long legacyImageId, int sortOrder = 0)
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO collection_images (collection_id, image_id, sort_order)
            SELECT c.id, $iid, $order FROM collections c WHERE c.name = $name;
            """;
        command.Parameters.AddWithValue("$name", collectionName);
        command.Parameters.AddWithValue("$iid", legacyImageId);
        command.Parameters.AddWithValue("$order", sortOrder);
        command.ExecuteNonQuery();
        return this;
    }

    public void Dispose()
    {
        try { File.Delete(_path); }
        catch (IOException) { /* best effort */ }
    }
}
