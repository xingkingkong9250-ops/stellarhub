using System.IO;
using QL.World.Services;

namespace QL.World.Tests;

/// <summary>
/// M5-F: verifies the LRU disk-cache trimmer deletes the oldest .webp
/// files when the thumbnails directory exceeds ThumbnailCacheLimitMb.
/// </summary>
public sealed class ThumbnailCacheServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly AppSettingsService _settings;

    public ThumbnailCacheServiceTests()
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
    public void TrimNow_deletes_oldest_files_when_over_limit()
    {
        // Arrange: create 5 files of 300 KB each in the same shard dir.
        // Total = 1.5 MB > 1 MB limit. The LRU trimmer should delete the
        // oldest (by LastWriteTime) until total <= 1 MB.
        string shardDir = Path.Combine(_directory, "thumbnails", "ab");
        Directory.CreateDirectory(shardDir);

        var files = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            // <hash>_thumb.webp naming so it's a valid cache file.
            string path = Path.Combine(shardDir, $"file{i:D2}_thumb.webp");
            byte[] data = new byte[300 * 1024]; // 300 KB
            File.WriteAllBytes(path, data);
            // Stagger LastWriteTime so file0 is oldest, file4 is newest.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-(5 - i)));
            files.Add(path);
        }

        // Also add a placeholder — must never be deleted.
        string placeholderPath = Path.Combine(shardDir, "placeholder.webp");
        File.WriteAllBytes(placeholderPath, new byte[1024]);

        var cache = new ThumbnailCacheService(_directory, _settings);

        // Act: force an immediate trim with a 1 MB limit (uses the internal
        // test overload that bypasses the [128, 8192] clamping).
        cache.TrimNow(1 * 1024 * 1024);

        // Assert: total remaining files (excluding placeholder) <= 1 MB.
        long totalRemaining = 0;
        int survivingThumbs = 0;
        foreach (string f in files)
        {
            if (File.Exists(f))
            {
                totalRemaining += new FileInfo(f).Length;
                survivingThumbs++;
            }
        }
        Assert.True(totalRemaining <= 1 * 1024 * 1024,
            $"Expected <= 1 MB remaining, got {totalRemaining} bytes across {survivingThumbs} files");
        // The oldest files should have been deleted first.
        Assert.False(File.Exists(files[0]), "file00 (oldest) should have been deleted");
        // The newest file should still exist.
        Assert.True(File.Exists(files[4]), "file04 (newest) should still exist");
        // Placeholder must survive.
        Assert.True(File.Exists(placeholderPath), "placeholder.webp must not be deleted");
    }

    [Fact]
    public void TrimNow_is_noop_when_under_limit()
    {
        _settings.ThumbnailCacheLimitMb = 100;

        string shardDir = Path.Combine(_directory, "thumbnails", "ab");
        Directory.CreateDirectory(shardDir);

        string path = Path.Combine(shardDir, "aabb001122334455_thumb.webp");
        File.WriteAllBytes(path, new byte[1024]);

        var cache = new ThumbnailCacheService(_directory, _settings);
        cache.TrimNow();

        Assert.True(File.Exists(path), "File should not be deleted when under limit");
    }

    [Fact]
    public void TrimNow_skips_partial_and_tmp_files()
    {
        // .partial and .tmp files are transients — the trimmer must skip them
        // (they belong to in-flight thumbnail generation).
        string shardDir = Path.Combine(_directory, "thumbnails", "ab");
        Directory.CreateDirectory(shardDir);

        // Create a large .partial file (should be ignored by the trimmer).
        string partialPath = Path.Combine(shardDir, "aabb_partial.webp.partial");
        File.WriteAllBytes(partialPath, new byte[2 * 1024 * 1024]); // 2 MB
        File.SetLastWriteTimeUtc(partialPath, DateTime.UtcNow.AddHours(-10));

        // Create a small _thumb.webp (well under limit).
        string thumbPath = Path.Combine(shardDir, "aabb_thumb.webp");
        File.WriteAllBytes(thumbPath, new byte[1024]);

        var cache = new ThumbnailCacheService(_directory, _settings);
        // Force a 1 MB limit — the .partial (2 MB) would exceed it if
        // counted, but the trimmer skips .partial files so the _thumb
        // (1 KB) should survive.
        cache.TrimNow(1 * 1024 * 1024);

        // The thumb file should survive (the .partial is ignored so total is tiny).
        Assert.True(File.Exists(thumbPath), "_thumb.webp should not be deleted");
    }

    [Fact]
    public void TrimNow_handles_empty_directory_gracefully()
    {
        _settings.ThumbnailCacheLimitMb = 1;

        // No thumbnails directory at all.
        var cache = new ThumbnailCacheService(_directory, _settings);
        cache.TrimNow(); // Must not throw.
    }

    [Fact]
    public void Start_and_Dispose_do_not_throw()
    {
        var cache = new ThumbnailCacheService(_directory, _settings);
        cache.Start();
        // Dispose immediately — the timer must not fire or must be no-op.
        cache.Dispose();
    }
}
