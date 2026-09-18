using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Services.Colors;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>Backfill semantics of ColorAnalysisService over a temp library.</summary>
public sealed class ColorAnalysisServiceTests : IDisposable
{
    private readonly TestLibrary _library = new();
    private readonly string _imageDir;
    private readonly ColorAnalysisService _service;

    public ColorAnalysisServiceTests()
    {
        _imageDir = Path.Combine(_library.DirectoryRoot, "images");
        _service = new ColorAnalysisService(_library.Db);
    }

    [Fact]
    public async Task Backfills_null_colors_for_existing_image()
    {
        string path = TestImageFactory.CreatePng(_imageDir, "a.png");
        long id = _library.InsertImage(path);

        bool wrote = await _service.AnalyzeAsync(id, path, CancellationToken.None);

        Assert.True(wrote);
        Assert.Equal("""["#C86432"]""", GetDominantColors(id));
    }

    [Fact]
    public async Task Does_not_overwrite_existing_colors()
    {
        string path = TestImageFactory.CreatePng(_imageDir, "b.png");
        long id = _library.InsertImage(path);
        SetDominantColors(id, """["#123456"]""");

        bool wrote = await _service.AnalyzeAsync(id, path, CancellationToken.None);

        Assert.False(wrote);
        Assert.Equal("""["#123456"]""", GetDominantColors(id));
    }

    [Fact]
    public async Task Missing_file_is_skipped_without_throwing()
    {
        long id = _library.InsertImage(); // default path points nowhere

        bool wrote = await _service.AnalyzeAsync(id, Path.Combine(_imageDir, "gone.png"), CancellationToken.None);

        Assert.False(wrote);
        Assert.Null(GetDominantColors(id));
    }

    [Fact]
    public async Task Garbage_file_is_skipped_without_throwing()
    {
        string path = TestImageFactory.CreateGarbage(_imageDir, "c.bin");
        long id = _library.InsertImage(path);

        bool wrote = await _service.AnalyzeAsync(id, path, CancellationToken.None);

        Assert.False(wrote);
        Assert.Null(GetDominantColors(id));
    }

    [Fact]
    public async Task RunPendingAsync_backfills_only_pending_and_is_idempotent()
    {
        string a = TestImageFactory.CreatePng(_imageDir, "a.png");
        string b = TestImageFactory.CreatePng(_imageDir, "b.png");
        long aId = _library.InsertImage(a);
        long bId = _library.InsertImage(b);
        _library.InsertImage(); // missing file stays pending but unwritable

        int first = await _service.RunPendingAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, first);
        Assert.NotNull(GetDominantColors(aId));
        Assert.NotNull(GetDominantColors(bId));

        int second = await _service.RunPendingAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(0, second); // backfilled rows are no longer pending
    }

    private string? GetDominantColors(long id) =>
        _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT dominant_colors FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            object? result = cmd.ExecuteScalar();
            return result is string value ? value : null;
        });

    private void SetDominantColors(long id, string json) =>
        _library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE images SET dominant_colors = $json WHERE id = $id;";
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public void Dispose()
    {
        _library.Dispose();
    }
}
