using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>0.16.0 DUP-A/B: 导入落库 phash 与导入时 L1 查重。</summary>
public sealed class ImportServicePhashTests
{
    [Fact]
    public async Task Import_writes_phash_column()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        string file = TestImageFactory.CreateGradientPng(source, "gradient.png");

        ImportReport report = await importer.ImportAsync(OptionsFor(source));

        Assert.Equal(1, report.AddedCount);

        string? stored = library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT phash FROM images LIMIT 1;";
            return command.ExecuteScalar() as string;
        });

        Assert.NotNull(stored);
        Assert.Equal(16, stored!.Length);
        Assert.Equal(
            new PerceptualHashService().TryCompute(file),
            PerceptualHashService.TryParseHex(stored));
    }

    [Fact]
    public async Task Corrupt_file_is_skipped_without_a_row()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreateGarbage(source, "broken.png");

        ImportReport report = await importer.ImportAsync(OptionsFor(source));

        Assert.Equal(0, report.AddedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(0L, CountImages(library));
    }

    [Fact]
    public async Task Import_reports_existing_near_duplicate_without_blocking()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());

        string firstDir = Path.Combine(library.DirectoryRoot, "first");
        TestImageFactory.CreateGradientPng(firstDir, "gradient.png");
        ImportReport first = await importer.ImportAsync(OptionsFor(firstDir));
        Assert.Equal(1, first.AddedCount);
        Assert.Empty(first.Duplicates);

        long existingId = library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText =
                "SELECT id FROM images WHERE file_name = 'gradient.png';";
            return Convert.ToInt64(command.ExecuteScalar());
        });

        // 同一画面的 JPEG 转码：字节不同（MD5 去重不拦），dHash 命中。
        string secondDir = Path.Combine(library.DirectoryRoot, "second");
        string variant = TestImageFactory.CreateGradientJpeg(secondDir, "gradient.jpg");
        ImportReport second = await importer.ImportAsync(OptionsFor(secondDir));

        // 只记录不拦截：新图照常入库。
        Assert.Equal(1, second.AddedCount);
        Assert.Equal(2L, CountImages(library));

        DuplicateHit hit = Assert.Single(second.Duplicates);
        Assert.Equal(existingId, hit.ExistingImageId);
        Assert.NotEqual(existingId, hit.ImageId);
        Assert.True(
            hit.HammingDistance <= PerceptualHashService.SameImageMaxDistance,
            $"同图距离应 ≤{PerceptualHashService.SameImageMaxDistance}，实际 {hit.HammingDistance}");
        Assert.Equal(Path.GetFullPath(variant), hit.FilePath);
        Assert.Contains(second.LogLines, line => line.Contains("疑似重复"));
    }

    [Fact]
    public async Task Duplicates_within_one_import_run_are_reported()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreateGradientPng(source, "gradient.png");
        TestImageFactory.CreateGradientJpeg(source, "gradient.jpg");

        ImportReport report = await importer.ImportAsync(OptionsFor(source));

        Assert.Equal(2, report.AddedCount);
        DuplicateHit hit = Assert.Single(report.Duplicates);
        Assert.True(hit.HammingDistance <= PerceptualHashService.SameImageMaxDistance);
    }

    [Fact]
    public async Task Distinct_images_report_no_duplicates()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreateGradientPng(source, "gradient.png");
        TestImageFactory.CreateCheckerboardPng(source, "checker.png");

        ImportReport report = await importer.ImportAsync(OptionsFor(source));

        Assert.Equal(2, report.AddedCount);
        Assert.Empty(report.Duplicates);
        Assert.DoesNotContain(report.LogLines, line => line.Contains("疑似重复"));
    }

    private static long CountImages(TestLibrary library) => library.Db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM images;";
        return Convert.ToInt64(command.ExecuteScalar());
    });

    private static ImportOptions OptionsFor(string root) => new()
    {
        InputPaths = [root],
        RecurseSubfolders = true,
        SkipHidden = true,
        EnabledExtensions = new HashSet<string>(
            ImportOptions.SupportedExtensions,
            StringComparer.OrdinalIgnoreCase),
    };
}
