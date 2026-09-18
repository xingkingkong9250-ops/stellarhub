using System.IO;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _directory;

    public AppSettingsServiceTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Temp cleanup is best effort. */ }
    }

    [Fact]
    public void New_installation_uses_known_defaults()
    {
        var settings = new AppSettingsService(_directory);
        settings.Load();

        Assert.True(settings.IsNewInstallation);
        Assert.Equal("Dark", settings.Theme);
        Assert.True(settings.CheckIntegrityOnStartup);
        Assert.Equal(200, settings.ThumbnailSize);
        Assert.Equal(82, settings.ThumbnailQuality);
        Assert.Equal("Grid", settings.GalleryViewMode);
        Assert.Equal("ImportedDesc", settings.GallerySort);
    }

    [Fact]
    public void Changes_are_persisted_across_instances()
    {
        var first = new AppSettingsService(_directory);
        first.Load();
        first.ThumbnailSize = 333;
        first.GalleryViewMode = "List";
        Assert.True(first.LastSaveSucceeded);

        var second = new AppSettingsService(_directory);
        second.Load();
        Assert.Equal(333, second.ThumbnailSize);
        Assert.Equal("List", second.GalleryViewMode);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_load()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"thumbnailSize\":9999,\"thumbnailQuality\":-5}");

        var settings = new AppSettingsService(_directory);
        settings.Load();

        Assert.Equal(512, settings.ThumbnailSize);
        Assert.Equal(30, settings.ThumbnailQuality);
    }

    [Fact]
    public void Unknown_theme_and_view_mode_fall_back_to_defaults()
    {
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            "{\"theme\":\"Neon\",\"galleryViewMode\":\"Mosaic\"}");

        var settings = new AppSettingsService(_directory);
        settings.Load();

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("Grid", settings.GalleryViewMode);
    }

    [Fact]
    public void Corrupt_primary_is_recovered_from_backup_snapshot()
    {
        var healthy = new AppSettingsService(_directory);
        healthy.Load();
        healthy.GallerySort = "NameAsc";

        File.WriteAllText(Path.Combine(_directory, "config.json"), "{ broken json");

        var recovered = new AppSettingsService(_directory);
        recovered.Load();

        Assert.Equal("NameAsc", recovered.GallerySort);
        // RestoreBackup() replaces the corrupt primary with the snapshot,
        // so the corrupt bytes must no longer be in place.
        Assert.NotEqual(
            "{ broken json",
            File.ReadAllText(Path.Combine(_directory, "config.json")));
    }
}
