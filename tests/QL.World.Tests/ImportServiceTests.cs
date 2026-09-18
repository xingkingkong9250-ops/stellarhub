using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class ImportServiceTests
{
    private static ImportOptions OptionsFor(string root, params string[] paths) => new()
    {
        InputPaths = paths.Length == 0 ? [root] : paths,
        RecurseSubfolders = true,
        SkipHidden = true,
        EnabledExtensions = new HashSet<string>(
            ImportOptions.SupportedExtensions,
            StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public async Task Imports_valid_images_and_skips_corrupt_files()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "good.png");
        TestImageFactory.CreateGarbage(source, "broken.png");

        ImportReport report = await importer.ImportAsync(OptionsFor(source));

        Assert.False(report.Cancelled);
        Assert.Equal(1, report.AddedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(1, new ImageQueryService(library.Db).TotalCount());
    }

    [Fact]
    public async Task Identical_content_in_two_files_is_imported_once()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "a.png");
        TestImageFactory.CreatePng(source, "copy_of_a.png");

        ImportReport report = await importer.ImportAsync(OptionsFor(source));

        Assert.Equal(1, report.AddedCount);
        Assert.Equal(1, report.SkippedCount);
    }

    [Fact]
    public async Task Second_run_is_idempotent()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "a.png");

        var first = await importer.ImportAsync(OptionsFor(source));
        var second = await importer.ImportAsync(OptionsFor(source));

        Assert.Equal(1, first.AddedCount);
        Assert.Equal(0, second.AddedCount);
        Assert.Equal(1, second.SkippedCount);
    }

    [Fact]
    public async Task Non_recursive_option_ignores_subfolders()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "top.png");
        TestImageFactory.CreatePng(Path.Combine(source, "sub"), "nested.png");

        var options = OptionsFor(source);
        var flat = new ImportOptions
        {
            InputPaths = [source],
            RecurseSubfolders = false,
            SkipHidden = true,
            EnabledExtensions = options.EnabledExtensions,
        };

        ImportReport report = await importer.ImportAsync(flat);

        Assert.Equal(1, report.AddedCount);
    }

    [Fact]
    public async Task Pre_cancelled_token_aborts_before_inserts()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "a.png");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ImportReport report = await importer.ImportAsync(
            OptionsFor(source), cancellationToken: cts.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(0, report.AddedCount);
        Assert.Equal(0, new ImageQueryService(library.Db).TotalCount());
    }

    [Fact]
    public async Task Hidden_files_are_skipped_when_option_enabled()
    {
        using var library = new TestLibrary();
        var importer = new ImportService(library.Db, new ImageInspector());
        string source = Path.Combine(library.DirectoryRoot, "source");
        string visible = TestImageFactory.CreatePng(source, "visible.png");
        string hidden = TestImageFactory.CreatePng(source, "hidden.png");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        ImportReport report = await importer.ImportAsync(OptionsFor(source));

        Assert.Equal(1, report.AddedCount);
        Assert.Equal(visible, report.Added[0].FilePath);
    }
}
