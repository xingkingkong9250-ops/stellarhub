using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class LibraryRelocationServiceTests : IDisposable
{
    private readonly string _root;

    public LibraryRelocationServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* Temp cleanup is best effort. */ }
    }

    [Fact]
    public void ValidateTarget_rejects_same_nested_nonempty_and_unconfigured()
    {
        (AppSettingsService settings, DatabaseService db, string source) = CreateLibrary();

        var service = new LibraryRelocationService(settings, db);
        Assert.Throws<InvalidOperationException>(
            () => service.ValidateTarget(source));

        string child = Path.Combine(source, "child");
        Directory.CreateDirectory(child);
        Assert.Throws<InvalidOperationException>(
            () => service.ValidateTarget(child));

        // Target that would contain the current library (would recursively copy).
        Assert.Throws<InvalidOperationException>(
            () => service.ValidateTarget(_root));

        string nonEmpty = Path.Combine(_root, "nonempty");
        Directory.CreateDirectory(nonEmpty);
        File.WriteAllText(Path.Combine(nonEmpty, "x.txt"), "x");
        Assert.Throws<InvalidOperationException>(
            () => service.ValidateTarget(nonEmpty));

        var unconfigured = new AppSettingsService(Path.Combine(_root, "cfg2"));
        unconfigured.Load();
        Assert.Throws<InvalidOperationException>(
            () => new LibraryRelocationService(unconfigured, db).ValidateTarget(nonEmpty));
    }

    [Fact]
    public async Task Relocate_copies_tree_rewrites_paths_and_switches_root()
    {
        (AppSettingsService settings, DatabaseService db, string source) = CreateLibrary();

        string thumbnail = Path.Combine(source, "thumbnails", "a.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
        File.WriteAllBytes(thumbnail, [1, 2, 3]);

        string physical = Path.Combine(
            source, "files", "ab", "abcd", "x_A0001.png");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllBytes(physical, [4, 5, 6, 7]);

        string categoryFile = Path.Combine(source, "素材", "x_A0001.png");
        Directory.CreateDirectory(Path.GetDirectoryName(categoryFile)!);
        File.Copy(physical, categoryFile);

        string externalOriginal = Path.Combine(_root, "external", "orig.png");
        Directory.CreateDirectory(Path.GetDirectoryName(externalOriginal)!);
        File.WriteAllBytes(externalOriginal, [9]);

        string now = "2026-01-01T00:00:00.000Z";

        db.RunInTransaction((conn, tx) =>
        {
            using (var library = conn.CreateCommand())
            {
                library.Transaction = tx;
                library.CommandText = """
                    INSERT INTO library_files
                        (content_hash, physical_path, file_size, entry_code, created_at)
                    VALUES ('abcd', $physical, 4, 'A0001', $now);
                    """;
                library.Parameters.AddWithValue("$physical", physical);
                library.Parameters.AddWithValue("$now", now);
                library.ExecuteNonQuery();
            }

            using var image = conn.CreateCommand();
            image.Transaction = tx;
            image.CommandText = """
                INSERT INTO images
                    (file_path, file_name, sortable_name, file_hash, file_size,
                     width, height, format, rating, notes, primary_category,
                     dominant_colors, is_missing, created_at, updated_at,
                     library_hash, library_path, original_path, entry_code)
                VALUES
                    ($file, 'x_A0001.png', 'x_a0001.png', 'abcd', 4,
                     10, 10, 'png', 0, '', '素材',
                     NULL, 0, $now, $now,
                     'abcd', $file, $original, 'A0001');
                """;
            image.Parameters.AddWithValue("$file", categoryFile);
            image.Parameters.AddWithValue("$original", externalOriginal);
            image.Parameters.AddWithValue("$now", now);
            image.ExecuteNonQuery();
        });

        string target = Path.Combine(_root, "newlib");
        var progress = new SyncProgress<LibraryRelocationProgress>();
        var service = new LibraryRelocationService(settings, db);

        LibraryRelocationResult result =
            await service.RelocateAsync(target, progress);

        Assert.Equal(Path.GetFullPath(target), result.NewRoot);
        Assert.Equal(4, result.FilesCopied);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(100, progress.Values[^1].Percent);

        Assert.True(File.Exists(Path.Combine(target, "thumbnails", "a.webp")));
        Assert.True(File.Exists(Path.Combine(target, "files", "ab", "abcd", "x_A0001.png")));
        Assert.True(File.Exists(Path.Combine(target, "素材", "x_A0001.png")));

        // Old folder stays as a backup.
        Assert.True(File.Exists(categoryFile));

        Assert.Equal(Path.GetFullPath(target), settings.LibraryRootPath);
        Assert.Equal(Path.GetFullPath(target), settings.DataRootPath);

        using var connection = OpenReadOnly(Path.Combine(target, "qlworld.db"));

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT file_path, library_path, original_path FROM images;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.StartsWith(Path.GetFullPath(target), reader.GetString(0));
            Assert.StartsWith(Path.GetFullPath(target), reader.GetString(1));
            Assert.Equal(externalOriginal, reader.GetString(2));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT physical_path FROM library_files;";
            Assert.StartsWith(
                Path.GetFullPath(target),
                Convert.ToString(command.ExecuteScalar()));
        }

        Assert.Equal("ok", QuickCheck(Path.Combine(target, "qlworld.db")));
    }

    [Fact]
    public async Task Relocate_rolls_back_on_copy_failure()
    {
        (AppSettingsService settings, DatabaseService db, string source) = CreateLibrary();

        string locked = Path.Combine(source, "files", "ab", "abcd", "locked.png");
        Directory.CreateDirectory(Path.GetDirectoryName(locked)!);
        File.WriteAllBytes(locked, [1, 2, 3]);

        string target = Path.Combine(_root, "rollback-target");
        var service = new LibraryRelocationService(settings, db);

        using (var hold = new FileStream(
                   locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<LibraryRelocationException>(
                () => service.RelocateAsync(target));
        }

        Assert.False(Directory.Exists(target));
        Assert.Equal(Path.GetFullPath(source), settings.LibraryRootPath);
        Assert.True(File.Exists(locked));
    }

    private (AppSettingsService Settings, DatabaseService Database, string Source)
        CreateLibrary()
    {
        string source = Path.Combine(_root, "lib");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "thumbnails"));
        Directory.CreateDirectory(Path.Combine(source, "models"));
        Directory.CreateDirectory(Path.Combine(source, "files"));

        string configRoot = Path.Combine(_root, "config");
        var settings = new AppSettingsService(configRoot);
        settings.Load();
        settings.ConfigureLibraryRoot(source);

        var database = new DatabaseService(source);
        database.Initialize();
        return (settings, database, source);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static string QuickCheck(string path)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
