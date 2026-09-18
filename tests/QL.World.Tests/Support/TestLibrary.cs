using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;

namespace QL.World.Tests.Support;

/// <summary>Temporary on-disk library for service tests.</summary>
internal sealed class TestLibrary : IDisposable
{
    private readonly string _directory;

    public TestLibrary()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Db = new DatabaseService(_directory);
        Db.Initialize();
    }

    public DatabaseService Db { get; }

    public string DirectoryRoot => _directory;

    /// <summary>Direct row insert bypassing the importer.</summary>
    public long InsertImage(
        string? filePath = null,
        string? hash = null,
        string? category = null,
        int rating = 0,
        string notes = "")
    {
        string path = filePath ?? Path.Combine(_directory, $"img_{Guid.NewGuid():N}.png");
        string fileHash = hash ?? Guid.NewGuid().ToString("N");
        string name = Path.GetFileName(path);
        string now = "2026-01-01T00:00:00.000Z";

        return Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                INSERT INTO images
                    (file_path, file_name, sortable_name, file_hash, file_size,
                     width, height, format, rating, notes, primary_category,
                     dominant_colors, is_missing, created_at, updated_at)
                VALUES
                    ($path, $name, $sortable, $hash, 123,
                     10, 10, 'png', $rating, $notes, $category,
                     NULL, 0, $now, $now);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$sortable", name.ToLowerInvariant());
            command.Parameters.AddWithValue("$hash", fileHash);
            command.Parameters.AddWithValue("$rating", rating);
            command.Parameters.AddWithValue("$notes", notes);
            command.Parameters.AddNullable("$category", category);
            command.Parameters.AddWithValue("$now", now);
            return Convert.ToInt64(command.ExecuteScalar());
        });
    }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Windows file lock cleanup is best effort. */ }
    }
}
