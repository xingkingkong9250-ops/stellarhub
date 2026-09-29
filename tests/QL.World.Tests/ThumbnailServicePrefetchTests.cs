using System.IO;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// M5-H: verifies the prefetch entry point of ThumbnailService. Prefetch
/// enqueues items at low priority for surrounding thumbnails (next scroll
/// batch). Existing thumbnails are skipped (no duplicate work); empty/null
/// hash entries are silently dropped.
/// </summary>
public sealed class ThumbnailServicePrefetchTests : IDisposable
{
    private readonly string _directory;
    private readonly AppSettingsService _settings;

    public ThumbnailServicePrefetchTests()
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
    public void Prefetch_enqueues_surrounding_items_for_thumb_generation()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string srcA = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "a.png", 40, 30);
        string srcB = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "b.png", 40, 30);
        const string hashA = "aaa1112223334445";
        const string hashB = "bbb2223334445556";

        service.RequestPrefetch(new[]
        {
            (hashA, (IImageSource)new LocalImageSource(srcA)),
            (hashB, (IImageSource)new LocalImageSource(srcB)),
        });

        string expectedA = Path.Combine(
            _directory, "thumbnails", "aa", $"{hashA}_thumb.webp");
        string expectedB = Path.Combine(
            _directory, "thumbnails", "bb", $"{hashB}_thumb.webp");
        Assert.True(WaitFor(() => File.Exists(expectedA), TimeSpan.FromSeconds(10)));
        Assert.True(WaitFor(() => File.Exists(expectedB), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Prefetch_skips_items_whose_thumb_already_exists()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string src = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "a.png", 40, 30);
        const string hash = "ccc3334445556667";

        // First request generates the thumb.
        service.Request(hash, new LocalImageSource(src), ThumbnailLevel.Thumb);
        string expected = Path.Combine(
            _directory, "thumbnails", "cc", $"{hash}_thumb.webp");
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));
        var firstWriteTime = new FileInfo(expected).LastWriteTimeUtc;

        // Prefetch the same item — should be a no-op (no re-enqueue).
        Thread.Sleep(200); // ensure timestamps would differ if rewritten
        service.RequestPrefetch(new[]
        {
            (hash, (IImageSource)new LocalImageSource(src)),
        });
        Thread.Sleep(500);
        Assert.Equal(
            firstWriteTime,
            new FileInfo(expected).LastWriteTimeUtc);
    }

    [Fact]
    public void Prefetch_silently_drops_empty_or_whitespace_hashes()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string src = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "a.png", 40, 30);
        const string validHash = "ddd4445556667778";

        // Empty/whitespace entries must not throw and must not enqueue work.
        service.RequestPrefetch(new[]
        {
            (string.Empty, (IImageSource)new LocalImageSource(src)),
            ("   ", (IImageSource)new LocalImageSource(src)),
            (validHash, (IImageSource)new LocalImageSource(src)),
        });

        string expected = Path.Combine(
            _directory, "thumbnails", "dd", $"{validHash}_thumb.webp");
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Prefetch_null_items_throws()
    {
        using var service = new ThumbnailService(_directory, _settings);
        Assert.Throws<ArgumentNullException>(
            () => service.RequestPrefetch(null!));
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
