using System.IO;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class LegacyPythonImporterTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly LegacyPythonImporter _importer;

    public LegacyPythonImporterTests() => _importer = new LegacyPythonImporter(_lib.Db);

    [Fact]
    public async Task Import_maps_columns_and_reports_counts()
    {
        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(@"X:\py\metal.png", "hash-a", category: "材质参考", rating: "3", notes: "旧备注")
            .AddImage(@"X:\py\ui.png", "hash-b", category: "UI/HUD", rating: "5")
            .AddTag(1, "metal")
            .AddTag(2, "hud", source: "manual")
            .AddCollection("旧收藏", legacyCoverImageId: 1)
            .AddCollectionImage("旧收藏", 1, sortOrder: 0);

        var report = await _importer.ImportAsync(legacy.DbPath);

        Assert.Null(report.ErrorMessage);
        Assert.False(report.Cancelled);
        Assert.Equal(2, report.ScannedTotal);
        Assert.Equal(2, report.AddedCount);
        Assert.Equal(0, report.SkippedCount);
        Assert.Equal(2, report.TagCount);
        Assert.Equal(1, report.CollectionCount);

        var query = new ImageQueryService(_lib.Db);
        Assert.Equal(2, query.TotalCount());

        var metal = query.GetById(1)!;
        Assert.Equal(@"X:\py\metal.png", metal.FilePath);
        Assert.Equal("metal.png", metal.FileName);
        Assert.Equal("metal.png", metal.SortableName);          // derived
        Assert.False(metal.IsMissing);                           // forced 0
        Assert.Equal("hash-a", metal.FileHash);
        Assert.Equal(3, metal.Rating);
        Assert.Equal("旧备注", metal.Notes);
        Assert.Equal("材质参考", metal.PrimaryCategory);
        Assert.Equal(2048, metal.FileSize);
        Assert.Equal(metal.CreatedAt, metal.UpdatedAt);          // updated_at = created_at

        // Python "2025-06-01 08:30:00" is written as UTC in the new library.
        Assert.Equal(
            new DateTime(2025, 6, 1, 8, 30, 0, DateTimeKind.Utc),
            metal.CreatedAt.ToUniversalTime());

        var collections = new CollectionService(_lib.Db);
        var all = collections.GetAll();
        Assert.Single(all);
        Assert.Equal("旧收藏", all[0].Name);
        Assert.Equal(1, all[0].ImageCount);
        Assert.Equal(1, collections.GetImageIds(all[0].Id)[0]);  // mapped to new id
    }

    [Fact]
    public async Task Import_dedupes_by_path_and_hash()
    {
        _lib.InsertImage(filePath: @"X:\py\metal.png", hash: "hash-a"); // path conflict
        _lib.InsertImage(filePath: @"X:\new\other.png", hash: "hash-b"); // hash conflict

        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(@"X:\py\metal.png", "hash-x")
            .AddImage(@"X:\py\ui.png", "hash-b")
            .AddImage(@"X:\py\fresh.png", "hash-c");

        var report = await _importer.ImportAsync(legacy.DbPath);

        Assert.Null(report.ErrorMessage);
        Assert.Equal(3, report.ScannedTotal);
        Assert.Equal(1, report.AddedCount);
        Assert.Equal(2, report.SkippedCount);

        var query = new ImageQueryService(_lib.Db);
        Assert.Equal(3, query.TotalCount()); // 2 pre-existing + 1 fresh
        Assert.Equal(3, query.FindByPath(@"X:\py\fresh.png"));
    }

    [Fact]
    public async Task Import_skips_images_turns_empty_category_into_null()
    {
        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(@"X:\py\none.png", "hash-n", category: null);

        await _importer.ImportAsync(legacy.DbPath);

        var query = new ImageQueryService(_lib.Db);
        Assert.Null(query.GetById(1)!.PrimaryCategory);
    }

    [Fact]
    public async Task Import_rerun_is_idempotent()
    {
        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(@"X:\py\metal.png", "hash-a")
            .AddTag(1, "metal")
            .AddCollection("旧收藏")
            .AddCollectionImage("旧收藏", 1);

        var first = await _importer.ImportAsync(legacy.DbPath);
        var second = await _importer.ImportAsync(legacy.DbPath);

        Assert.Equal(1, first.AddedCount);
        Assert.Equal(0, second.AddedCount);
        Assert.Equal(1, second.ScannedTotal);

        var query = new ImageQueryService(_lib.Db);
        Assert.Equal(1, query.TotalCount());

        var collections = new CollectionService(_lib.Db);
        Assert.Single(collections.GetAll());
    }

    [Fact]
    public async Task Import_returns_error_for_missing_file()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ql-world-tests", "no-such-legacy.db");

        var report = await _importer.ImportAsync(missing);

        Assert.NotNull(report.ErrorMessage);
        Assert.Contains("不存在", report.ErrorMessage);
        Assert.Equal(0, report.ScannedTotal);
    }

    [Fact]
    public async Task Import_rejects_non_ql_database()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", $"random_{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (id INTEGER);";
            command.ExecuteNonQuery();
        }

        try
        {
            var report = await _importer.ImportAsync(path);
            Assert.NotNull(report.ErrorMessage);
            Assert.Contains("不是有效", report.ErrorMessage);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); }
            catch (IOException) { /* pool release timing is best effort */ }
        }
    }

    [Fact]
    public async Task Import_without_tags_leaves_tags_empty()
    {
        // The builder creates image_embeddings; importer never reads it.
        using var legacy = new LegacyPythonDbBuilder()
            .AddImage(@"X:\py\emb.png", "hash-e");

        var report = await _importer.ImportAsync(legacy.DbPath);

        Assert.Null(report.ErrorMessage);
        Assert.Equal(1, report.AddedCount);
        var tags = new TagService(_lib.Db);
        Assert.Empty(tags.GetTagsForImage(1));
    }

    public void Dispose() => _lib.Dispose();
}
