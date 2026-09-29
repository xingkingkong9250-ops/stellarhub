using System.IO;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// M5-F: verifies multi-level thumbnail generation (Thumb=256px, Preview=1024px)
/// and legacy &lt;hash&gt;.webp → &lt;hash&gt;_thumb.webp migration.
/// </summary>
public sealed class ThumbnailServiceLevelTests : IDisposable
{
    private readonly string _directory;
    private readonly AppSettingsService _settings;

    public ThumbnailServiceLevelTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new AppSettingsService(_directory);
        _settings.Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Best effort. */ }
    }

    [Fact]
    public async Task Request_thumb_level_generates_thumb_webp()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "a.png", 40, 30);
        const string hash = "aaa1112223334445";

        service.Request(hash, new LocalImageSource(source), ThumbnailLevel.Thumb);

        string expected = Path.Combine(
            _directory, "thumbnails", "aa", $"{hash}_thumb.webp");
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));
        Assert.True(new FileInfo(expected).Length > 0);
    }

    [Fact]
    public async Task Request_preview_level_generates_preview_webp()
    {
        using var service = new ThumbnailService(_directory, _settings);
        // Large enough that downscale to 1024 actually happens.
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "big.png", 2048, 1536);
        const string hash = "bbb2223334445556";

        service.Request(hash, new LocalImageSource(source), ThumbnailLevel.Preview);

        string expected = Path.Combine(
            _directory, "thumbnails", "bb", $"{hash}_preview.webp");
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));
        Assert.True(new FileInfo(expected).Length > 0);
    }

    [Fact]
    public async Task Thumb_and_preview_can_coexist_for_same_hash()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "both.png", 2048, 1536);
        const string hash = "ccc3334445556667";

        service.Request(hash, new LocalImageSource(source), ThumbnailLevel.Thumb);
        service.Request(hash, new LocalImageSource(source), ThumbnailLevel.Preview);

        string thumbPath = Path.Combine(
            _directory, "thumbnails", "cc", $"{hash}_thumb.webp");
        string previewPath = Path.Combine(
            _directory, "thumbnails", "cc", $"{hash}_preview.webp");

        Assert.True(WaitFor(() => File.Exists(thumbPath), TimeSpan.FromSeconds(10)));
        Assert.True(WaitFor(() => File.Exists(previewPath), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ThumbnailReady_includes_level_for_thumb()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "a.png", 40, 30);
        const string hash = "ddd4445556667778";

        ThumbnailLevel? raisedLevel = null;
        string? raisedHash = null;
        service.ThumbnailReady += (h, level) =>
        {
            raisedHash = h;
            raisedLevel = level;
        };

        service.Request(hash, new LocalImageSource(source), ThumbnailLevel.Thumb);

        Assert.True(WaitFor(
            () => File.Exists(service.GetThumbnailPath(hash, ThumbnailLevel.Thumb)),
            TimeSpan.FromSeconds(10)));
        await Task.Delay(200); // Give the worker a beat to raise the event.

        Assert.Equal(hash, raisedHash);
        Assert.Equal(ThumbnailLevel.Thumb, raisedLevel);
    }

    [Fact]
    public async Task ThumbnailReady_includes_level_for_preview()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "big.png", 2048, 1536);
        const string hash = "eee5556667778889";

        ThumbnailLevel? raisedLevel = null;
        service.ThumbnailReady += (_, level) => raisedLevel = level;

        service.Request(hash, new LocalImageSource(source), ThumbnailLevel.Preview);

        Assert.True(WaitFor(
            () => File.Exists(service.GetThumbnailPath(hash, ThumbnailLevel.Preview)),
            TimeSpan.FromSeconds(10)));
        await Task.Delay(200);

        Assert.Equal(ThumbnailLevel.Preview, raisedLevel);
    }

    [Fact]
    public void Legacy_hash_webp_migrated_to_thumb_on_startup()
    {
        // Arrange: create a legacy <hash>.webp in the thumbnails directory
        // (simulating a pre-M5-F library).
        string shardDir = Path.Combine(_directory, "thumbnails", "ab");
        Directory.CreateDirectory(shardDir);
        string legacyPath = Path.Combine(shardDir, "abcdef1234567890.webp");
        File.WriteAllText(legacyPath, "fake webp bytes");

        // Act: construct ThumbnailService — this triggers MigrateLegacyThumbnails.
        using var service = new ThumbnailService(_directory, _settings);

        // Assert: the legacy file was renamed to _thumb.webp.
        string thumbPath = Path.Combine(shardDir, "abcdef1234567890_thumb.webp");
        Assert.True(File.Exists(thumbPath));
        Assert.False(File.Exists(legacyPath));
    }

    [Fact]
    public void Legacy_migration_is_idempotent()
    {
        string shardDir = Path.Combine(_directory, "thumbnails", "ab");
        Directory.CreateDirectory(shardDir);
        string legacyPath = Path.Combine(shardDir, "aabbccddeeff0011.webp");
        File.WriteAllText(legacyPath, "fake webp bytes");

        // First construction migrates.
        using (var service = new ThumbnailService(_directory, _settings))
        {
            // Service is constructed; migration runs in constructor.
        }

        string thumbPath = Path.Combine(shardDir, "aabbccddeeff0011_thumb.webp");
        Assert.True(File.Exists(thumbPath));
        Assert.False(File.Exists(legacyPath));

        // Second construction should not re-migrate (no-op).
        using (var service = new ThumbnailService(_directory, _settings))
        {
        }
        Assert.True(File.Exists(thumbPath));
        // Legacy file still gone.
        Assert.False(File.Exists(legacyPath));
    }

    [Fact]
    public void Migration_skips_already_suffixed_files()
    {
        string shardDir = Path.Combine(_directory, "thumbnails", "ab");
        Directory.CreateDirectory(shardDir);
        // Already has _thumb suffix — must not be touched.
        string thumbPath = Path.Combine(shardDir, "aabb112233445566_thumb.webp");
        File.WriteAllText(thumbPath, "thumb bytes");
        string previewPath = Path.Combine(shardDir, "aabb112233445566_preview.webp");
        File.WriteAllText(previewPath, "preview bytes");

        using var service = new ThumbnailService(_directory, _settings);

        // Both files should still exist exactly as they were.
        Assert.True(File.Exists(thumbPath));
        Assert.True(File.Exists(previewPath));
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
