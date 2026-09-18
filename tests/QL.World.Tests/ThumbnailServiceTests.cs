using System.IO;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly AppSettingsService _settings;

    public ThumbnailServiceTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
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
    public async Task Request_generates_webp_under_hash_shard()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "a.png", 40, 30);
        const string hash = "abcdef1234567890";

        service.Request(hash, source);

        string expected = Path.Combine(
            _directory, "thumbnails", "ab", $"{hash}.webp");
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));

        var info = new FileInfo(expected);
        Assert.True(info.Length > 0);
    }

    [Fact]
    public async Task Missing_source_produces_placeholder_thumbnail()
    {
        using var service = new ThumbnailService(_directory, _settings);
        const string hash = "0011223344556677";

        service.Request(hash, Path.Combine(_directory, "gone.png"));

        string expected = service.GetThumbnailPath(hash);
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Regenerate_rebuilds_existing_webp()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "a.png", 20, 20);
        const string hash = "9988776655443322";

        service.Request(hash, source);
        string expected = service.GetThumbnailPath(hash);
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));

        DateTime firstWrite = File.GetLastWriteTimeUtc(expected);
        await Task.Delay(40);
        service.Regenerate(hash, source);

        Assert.True(WaitFor(
            () => File.Exists(expected) && File.GetLastWriteTimeUtc(expected) > firstWrite,
            TimeSpan.FromSeconds(10)));
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
