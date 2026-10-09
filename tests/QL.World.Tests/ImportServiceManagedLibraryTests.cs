using System.IO;
using System.Text.RegularExpressions;
using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// M11-D: ImportService managed-library behavior — CAS physical copy,
/// category hardlink, entry codes, (hash, category) dedupe. The library
/// root and the SQLite data root both live under the test temp directory,
/// i.e. the same NTFS volume required for hardlinks.
/// </summary>
public sealed class ImportServiceManagedLibraryTests : IDisposable
{
    private readonly string _workRoot;
    private readonly string _libraryRoot;
    private readonly TestLibrary _library;
    private readonly ImportService _importer;

    public ImportServiceManagedLibraryTests()
    {
        _workRoot = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot);
        _library = new TestLibrary();
        // TestLibrary owns the temp DB directory; the managed root lives
        // beside it under the same temp volume (NTFS hardlink requirement).
        _libraryRoot = Path.Combine(_library.DirectoryRoot, "managed");
        _importer = new ImportService(
            _library.Db, new ImageInspector(), pbrGrouping: null,
            libraryRootPath: _libraryRoot);
    }

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_workRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static ImportOptions Options(string source, string? category) => new()
    {
        InputPaths = [source],
        RecurseSubfolders = true,
        SkipHidden = true,
        PrimaryCategory = category,
        EnabledExtensions = new HashSet<string>(
            ImportOptions.SupportedExtensions,
            StringComparer.OrdinalIgnoreCase),
    };

    private static readonly Regex EntryCodePattern =
        new(@"^\d{8}-[0-9a-f]{6}-\d{3}$", RegexOptions.Compiled);

    private (string FilePath, string? LibraryPath, string OriginalPath,
        string? EntryCode, string? Category) SelectRow(string hash) =>
        _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT file_path, library_path, original_path, entry_code, primary_category
                FROM images WHERE file_hash = $hash;
                """;
            cmd.Parameters.AddWithValue("$hash", hash);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        });

    private long CountTable(string table) =>
        _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt64(cmd.ExecuteScalar());
        });

    [Fact]
    public async Task Managed_import_copies_bytes_and_creates_category_hardlink()
    {
        string source = Path.Combine(_workRoot, "source");
        string sourceFile = TestImageFactory.CreatePng(source, "a.png");
        byte[] sourceBytes = File.ReadAllBytes(sourceFile);

        ImportReport report = await _importer.ImportAsync(Options(source, "角色"));

        Assert.False(report.Cancelled);
        Assert.Equal(1, report.AddedCount);
        Assert.Equal(0, report.SkippedCount);

        string hash = report.Added[0].FileHash;
        var (filePath, libraryPath, originalPath, entryCode, category) = SelectRow(hash);

        // 分类层硬链接路径作为 file_path，文件名保留原名并追加入库编码。
        Assert.StartsWith(Path.Combine(_libraryRoot, "角色"), filePath);
        string linkName = Path.GetFileNameWithoutExtension(filePath);
        Assert.StartsWith("a_", linkName);
        Assert.EndsWith(entryCode, linkName);
        Assert.Equal(".png", Path.GetExtension(filePath));
        Assert.Equal(filePath, libraryPath);
        Assert.Equal(sourceFile, originalPath);
        Assert.NotNull(entryCode);
        Assert.Matches(EntryCodePattern, entryCode);
        Assert.Equal("角色", category);

        // 物理层 + 硬链接均落盘，字节一致；原始外部文件保留（复制而非移动）。
        Assert.True(File.Exists(filePath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(filePath));
        Assert.True(File.Exists(sourceFile));

        long physicalCount = CountTable("library_files");
        Assert.Equal(1, physicalCount);

        string physicalPath = _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT physical_path FROM library_files WHERE content_hash = $h;";
            cmd.Parameters.AddWithValue("$h", hash);
            return (string)cmd.ExecuteScalar()!;
        });
        Assert.StartsWith(Path.Combine(_libraryRoot, "files"), physicalPath);
        Assert.True(File.Exists(physicalPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(physicalPath));
        Assert.EndsWith($"{entryCode}.png", physicalPath);
    }

    [Fact]
    public async Task Managed_import_without_category_uses_uncategorized_and_null_column()
    {
        string source = Path.Combine(_workRoot, "source-uncat");
        TestImageFactory.CreatePng(source, "b.png");

        ImportReport report = await _importer.ImportAsync(Options(source, null));

        Assert.Equal(1, report.AddedCount);
        string hash = report.Added[0].FileHash;
        var (filePath, _, _, _, category) = SelectRow(hash);

        Assert.Contains(LibraryRootResolver.UncategorizedCategoryName, filePath);
        Assert.Null(category);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task Same_bytes_in_different_categories_share_one_physical_file()
    {
        string sourceA = Path.Combine(_workRoot, "srcA");
        string sourceB = Path.Combine(_workRoot, "srcB");
        string fileA = TestImageFactory.CreatePng(sourceA, "a.png");
        Directory.CreateDirectory(sourceB);
        string fileB = Path.Combine(sourceB, "b.png");
        File.Copy(fileA, fileB); // identical bytes, different source path

        var run1 = await _importer.ImportAsync(Options(sourceA, "角色"));
        var run2 = await _importer.ImportAsync(Options(sourceB, "场景"));

        Assert.Equal(1, run1.AddedCount);
        Assert.Equal(1, run2.AddedCount);
        Assert.Equal(2, CountTable("images"));
        Assert.Equal(1, CountTable("library_files")); // CAS: one physical file

        // Two category hardlinks, both readable with identical bytes.
        byte[] expected = File.ReadAllBytes(fileA);
        var links = new List<string>();
        _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT library_path FROM images ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string link = reader.GetString(0);
                links.Add(link);
                Assert.True(File.Exists(link));
                Assert.Equal(expected, File.ReadAllBytes(link));
            }
        });
        Assert.Equal(2, links.Count);
        Assert.Contains(links, p => p.Contains(Path.Combine(_libraryRoot, "角色")));
        Assert.Contains(links, p => p.Contains(Path.Combine(_libraryRoot, "场景")));

        // Exactly one file under the physical files tree.
        string[] physicalFiles = Directory.GetFiles(
            Path.Combine(_libraryRoot, "files"), "*", SearchOption.AllDirectories);
        Assert.Single(physicalFiles);
    }

    [Fact]
    public async Task Same_bytes_same_category_on_rerun_is_skipped()
    {
        string source = Path.Combine(_workRoot, "src-repeat");
        TestImageFactory.CreatePng(source, "a.png");

        var first = await _importer.ImportAsync(Options(source, "角色"));
        var second = await _importer.ImportAsync(Options(source, "角色"));

        Assert.Equal(1, first.AddedCount);
        Assert.Equal(0, second.AddedCount);
        Assert.Equal(1, second.SkippedCount);
        Assert.Equal(1, CountTable("images"));
        Assert.Equal(1, CountTable("library_files"));
    }

    [Fact]
    public async Task Two_distinct_images_get_distinct_entry_codes()
    {
        string source = Path.Combine(_workRoot, "src-two");
        // Identical factory PNGs differ only when pixels differ.
        TestImageFactory.CreatePng(source, "a.png", 12, 8);
        TestImageFactory.CreatePng(source, "b.png", 10, 6);

        ImportReport report = await _importer.ImportAsync(Options(source, "角色"));

        Assert.Equal(2, report.AddedCount);
        var codes = new List<string>();
        _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT entry_code FROM images ORDER BY file_name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                codes.Add(reader.GetString(0));
        });
        Assert.Equal(2, codes.Count);
        Assert.All(codes, c => Assert.Matches(EntryCodePattern, c));
        Assert.Equal(2, codes.Distinct().Count());
    }
}
