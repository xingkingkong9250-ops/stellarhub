using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>
/// F-08 regressions for <see cref="GalleryThumbnailProvider"/>:
/// 1. two gallery rows sharing one FileHash (identical images) must both
///    receive their bitmap — a hash-keyed realized map let them evict each
///    other and left cards blank;
/// 2. a corrupt on-disk WebP must never leave a permanent blank card
///    (placeholder fallback + bounded regeneration recovers it).
/// </summary>
public sealed class GalleryThumbnailProviderTests : IDisposable
{
    private readonly string _directory;
    private readonly AppSettingsService _settings;

    public GalleryThumbnailProviderTests()
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

    private static GalleryItemViewModel MakeItem(long id, string hash, string filePath)
        => new(new GalleryListItem
        {
            Id = id,
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            FileHash = hash,
            Source = ImageSource.Local,
        });

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void Attach_duplicate_hash_rows_both_receive_thumbnail()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "dup.png", 40, 30);
        const string hash = "aabbccddeeff0011";

        service.Request(hash, new LocalImageSource(source));
        string webp = service.GetThumbnailPath(hash);
        Assert.True(WaitFor(() => File.Exists(webp), TimeSpan.FromSeconds(10)));

        using var provider = new GalleryThumbnailProvider(service);
        var first = MakeItem(1, hash, source);
        var second = MakeItem(2, hash, source);

        provider.Attach(first);
        provider.Attach(second);

        bool bothLoaded = WaitFor(
            () => first.ThumbnailImage is not null && second.ThumbnailImage is not null,
            TimeSpan.FromSeconds(10));

        Assert.True(bothLoaded,
            $"first={(first.ThumbnailImage is not null)} second={(second.ThumbnailImage is not null)}");
    }

    [Fact]
    public void Recycling_churn_keeps_every_attached_row_painted()
    {
        using var service = new ThumbnailService(_directory, _settings);
        const string hash = "deadc0ffee001122";
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "churn.png", 32, 32);
        service.Request(hash, new LocalImageSource(source));
        string webp = service.GetThumbnailPath(hash);
        Assert.True(WaitFor(() => File.Exists(webp), TimeSpan.FromSeconds(10)));

        using var provider = new GalleryThumbnailProvider(service);
        var live = new List<GalleryItemViewModel>();
        var rng = new Random(20240608);

        // Hammer Attach/Detach like VirtualizingTilePanel recycling during a
        // fast pixel-scroll: rows with identical hashes rotate through a
        // handful of "realized" slots. No row that ends attached may stay blank.
        for (long id = 1; id <= 300; id++)
        {
            var item = MakeItem(id, hash, source);
            if (live.Count > 0 && rng.Next(2) == 0)
            {
                int victimIndex = rng.Next(live.Count);
                provider.Detach(live[victimIndex]);
                live.RemoveAt(victimIndex);
            }
            provider.Attach(item);
            live.Add(item);
            if (live.Count > 12)
            {
                provider.Detach(live[0]);
                live.RemoveAt(0);
            }
            if (id % 5 == 0)
                Thread.Sleep(2);
        }

        Assert.True(WaitFor(
            () => live.All(item => item.ThumbnailImage is not null),
            TimeSpan.FromSeconds(15)),
            $"blank attached rows: {live.Count(i => i.ThumbnailImage is null)}/{live.Count}");
    }

    [Fact]
    public void Corrupt_webp_falls_back_to_placeholder_then_recovers()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string source = TestImageFactory.CreatePng(
            Path.Combine(_directory, "src"), "ok.png", 24, 24);
        const string hash = "1122334455667788";

        // Produce a valid WebP first, then corrupt it on disk (simulates a
        // truncated/partial publication or disk corruption).
        service.Request(hash, new LocalImageSource(source));
        string webp = service.GetThumbnailPath(hash);
        Assert.True(WaitFor(() => File.Exists(webp), TimeSpan.FromSeconds(10)));
        File.WriteAllBytes(webp, new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0 });

        using var provider = new GalleryThumbnailProvider(service);
        var item = MakeItem(7, hash, source);

        provider.Attach(item);

        // The card must not stay blank: placeholder first, then the bounded
        // regeneration rebuilds the real thumbnail from the valid source.
        Assert.True(WaitFor(() => item.ThumbnailImage is not null,
            TimeSpan.FromSeconds(15)),
            "corrupt WebP left the card permanently blank");
    }
}
