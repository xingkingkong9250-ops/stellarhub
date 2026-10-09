using System.IO;
using System.Text.RegularExpressions;
using QL.World.Helpers;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class LegacyDataMigratorTests : IDisposable
{
    private readonly string _workRoot;
    private readonly string _managedRoot;
    private readonly string _sourceRoot;
    private readonly TestLibrary _library;

    public LegacyDataMigratorTests()
    {
        _workRoot = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_workRoot, "source");
        Directory.CreateDirectory(_sourceRoot);
        _library = new TestLibrary();
        _managedRoot = Path.Combine(_library.DirectoryRoot, "managed");
    }

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_workRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static readonly Regex EntryCodePattern =
        new(@"^\d{8}-[0-9a-f]{6}-\d{3}$", RegexOptions.Compiled);

    private long InsertImageRow(string sourcePath, string? category, string source = "local")
    {
        string hash = Md5Hasher.ComputeFile(sourcePath);
        long id = _library.InsertImage(
            filePath: sourcePath, hash: hash, category: category);
        if (source != "local")
        {
            _library.Db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE images SET source = 'remote' WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            });
        }
        return id;
    }

    private (string? LibraryPath, string? EntryCode) SelectLibraryColumns(long id) =>
        _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT library_path, entry_code FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
        });

    [Fact]
    public void Migrates_local_existing_file_into_library_with_category_hardlink()
    {
        string file = TestImageFactory.CreatePng(_sourceRoot, "a.png");
        long id = InsertImageRow(file, "角色");
        byte[] bytes = File.ReadAllBytes(file);

        var migrator = new LegacyDataMigrator(_library.Db, _managedRoot);
        LegacyDataMigrator.Result result = migrator.Run();

        Assert.Equal(1, result.TotalCandidates);
        Assert.Equal(1, result.Migrated);
        Assert.Equal(0, result.SkippedMissing);
        Assert.Equal(0, result.SkippedRemote);

        var (libraryPath, entryCode) = SelectLibraryColumns(id);
        Assert.NotNull(libraryPath);
        Assert.NotNull(entryCode);
        Assert.Matches(EntryCodePattern, entryCode);
        Assert.Contains(Path.Combine(_managedRoot, "角色"), libraryPath);
        Assert.True(File.Exists(libraryPath));
        Assert.Equal(bytes, File.ReadAllBytes(libraryPath));

        // 原始文件保留（复制而非移动），library_files 登记一条。
        Assert.True(File.Exists(file));
        long filesCount = _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM library_files;";
            return Convert.ToInt64(cmd.ExecuteScalar());
        });
        Assert.Equal(1, filesCount);
    }

    [Fact]
    public void Null_category_lands_in_uncategorized_folder()
    {
        string file = TestImageFactory.CreatePng(_sourceRoot, "b.png");
        long id = InsertImageRow(file, null);

        var result = new LegacyDataMigrator(_library.Db, _managedRoot).Run();

        Assert.Equal(1, result.Migrated);
        var (libraryPath, _) = SelectLibraryColumns(id);
        Assert.Contains(LibraryRootResolver.UncategorizedCategoryName, libraryPath);
    }

    [Fact]
    public void Missing_source_file_is_skipped_and_row_untouched()
    {
        string file = TestImageFactory.CreatePng(_sourceRoot, "gone.png");
        long id = InsertImageRow(file, null);
        File.Delete(file);

        var result = new LegacyDataMigrator(_library.Db, _managedRoot).Run();

        Assert.Equal(1, result.SkippedMissing);
        Assert.Equal(0, result.Migrated);
        var (libraryPath, _) = SelectLibraryColumns(id);
        Assert.Null(libraryPath);
    }

    [Fact]
    public void Remote_rows_are_skipped_per_nas_bypass()
    {
        string file = TestImageFactory.CreatePng(_sourceRoot, "remote.png");
        long id = InsertImageRow(file, "角色", source: "remote");

        var result = new LegacyDataMigrator(_library.Db, _managedRoot).Run();

        Assert.Equal(1, result.SkippedRemote);
        Assert.Equal(0, result.Migrated);
        var (libraryPath, _) = SelectLibraryColumns(id);
        Assert.Null(libraryPath);
    }

    [Fact]
    public void Second_run_is_idempotent_and_finds_no_candidates()
    {
        string file = TestImageFactory.CreatePng(_sourceRoot, "a.png");
        InsertImageRow(file, "角色");

        var migrator = new LegacyDataMigrator(_library.Db, _managedRoot);
        LegacyDataMigrator.Result first = migrator.Run();
        LegacyDataMigrator.Result second = migrator.Run();

        Assert.Equal(1, first.Migrated);
        Assert.Equal(0, second.TotalCandidates);
        Assert.Equal(0, second.Migrated);
    }

    [Fact]
    public void Pre_cancelled_token_aborts_before_changes()
    {
        string file = TestImageFactory.CreatePng(_sourceRoot, "a.png");
        long id = InsertImageRow(file, "角色");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new LegacyDataMigrator(_library.Db, _managedRoot)
                .Run(cancellationToken: cts.Token));

        var (libraryPath, _) = SelectLibraryColumns(id);
        Assert.Null(libraryPath);
    }

    [Fact]
    public void Mixed_batch_migrates_local_and_skips_remote_and_missing()
    {
        string ok = TestImageFactory.CreatePng(_sourceRoot, "ok.png");
        string remote = TestImageFactory.CreatePng(_sourceRoot, "remote.png");
        string gone = TestImageFactory.CreatePng(_sourceRoot, "gone.png");
        InsertImageRow(ok, "角色");
        InsertImageRow(remote, "角色", source: "remote");
        long goneId = InsertImageRow(gone, null);
        File.Delete(gone);

        var result = new LegacyDataMigrator(_library.Db, _managedRoot).Run();

        Assert.Equal(3, result.TotalCandidates);
        Assert.Equal(1, result.Migrated);
        Assert.Equal(1, result.SkippedRemote);
        Assert.Equal(1, result.SkippedMissing);
        var (gonePath, _) = SelectLibraryColumns(goneId);
        Assert.Null(gonePath);
    }
}
