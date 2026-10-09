using System.IO;
using System.Text.RegularExpressions;
using QL.World.Helpers;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// M11-F: LegacyPythonImporter 在配置库根后走与正常导入一致的托管语义
/// （CAS 副本 + 分类硬链接 + 编码 + library_files 登记）；源文件缺失时
/// 回落旧版直存，保留元数据。
/// </summary>
public sealed class LegacyPythonImporterManagedTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly string _managedRoot;
    private readonly string _sourceRoot;

    private static readonly Regex EntryCodePattern =
        new(@"^\d{8}-[0-9a-f]{6}-\d{3}$", RegexOptions.Compiled);

    public LegacyPythonImporterManagedTests()
    {
        _managedRoot = Path.Combine(_lib.DirectoryRoot, "managed");
        _sourceRoot = Path.Combine(_lib.DirectoryRoot, "py-pictures");
        Directory.CreateDirectory(_sourceRoot);
    }

    public void Dispose() => _lib.Dispose();

    private (string? LibraryPath, string? OriginalPath, string? EntryCode) SelectColumns()
        => _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT library_path, original_path, entry_code FROM images;";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        });

    [Fact]
    public async Task Managed_import_copies_bytes_and_registers_library_row()
    {
        string sourceFile = TestImageFactory.CreatePng(_sourceRoot, "metal.png");
        string hash = Md5Hasher.ComputeFile(sourceFile);
        byte[] bytes = File.ReadAllBytes(sourceFile);

        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(sourceFile, hash, category: "材质参考");

        var importer = new LegacyPythonImporter(_lib.Db, _managedRoot);
        var report = await importer.ImportAsync(legacy.DbPath);

        Assert.Null(report.ErrorMessage);
        Assert.Equal(1, report.AddedCount);

        var (libraryPath, originalPath, entryCode) = SelectColumns();
        Assert.NotNull(libraryPath);
        Assert.NotNull(entryCode);
        Assert.Matches(EntryCodePattern, entryCode);
        Assert.Equal(sourceFile, originalPath);
        Assert.Contains(Path.Combine(_managedRoot, "材质参考"), libraryPath);
        Assert.True(File.Exists(libraryPath));
        Assert.Equal(bytes, File.ReadAllBytes(libraryPath));
        Assert.True(File.Exists(sourceFile)); // 原文件保留

        long libraryFiles = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM library_files WHERE content_hash = $h;";
            cmd.Parameters.AddWithValue("$h", hash);
            return Convert.ToInt64(cmd.ExecuteScalar());
        });
        Assert.Equal(1, libraryFiles);
    }

    [Fact]
    public async Task Missing_source_falls_back_to_legacy_registration()
    {
        string sourceFile = TestImageFactory.CreatePng(_sourceRoot, "gone.png");
        string hash = Md5Hasher.ComputeFile(sourceFile);
        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(sourceFile, hash, category: null);
        File.Delete(sourceFile);

        var importer = new LegacyPythonImporter(_lib.Db, _managedRoot);
        var report = await importer.ImportAsync(legacy.DbPath);

        Assert.Equal(1, report.AddedCount);
        var (libraryPath, originalPath, entryCode) = SelectColumns();
        Assert.Null(libraryPath);
        Assert.Null(originalPath);
        Assert.Null(entryCode);
    }

    [Fact]
    public async Task Without_library_root_columns_stay_null()
    {
        string sourceFile = TestImageFactory.CreatePng(_sourceRoot, "plain.png");
        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(sourceFile, "any-hash");

        var importer = new LegacyPythonImporter(_lib.Db);
        var report = await importer.ImportAsync(legacy.DbPath);

        Assert.Equal(1, report.AddedCount);
        var (libraryPath, _, _) = SelectColumns();
        Assert.Null(libraryPath);
    }
}
