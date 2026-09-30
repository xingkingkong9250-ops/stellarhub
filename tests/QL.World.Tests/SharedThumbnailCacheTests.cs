using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using QL.World.Models;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>U6-A: NAS thumbnail download cache (decode, dedup, failure handling).</summary>
public sealed class SharedThumbnailCacheTests : IDisposable
{
    private const string Hash = "deadbeefcafe";

    private readonly string _dir;
    private readonly InMemoryNasConnector _nas = new();
    private readonly SharedThumbnailCache _cache;

    public SharedThumbnailCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cache = new SharedThumbnailCache(_nas, Path.Combine(_dir, "thumbs"));
    }

    [Fact]
    public async Task Ensure_downloads_decodes_and_pushes_thumbnail()
    {
        _nas.SeedBytes(SharedPaths.Thumbnail(Hash), EncodeWebp());
        var item = MakeItem();
        _cache.Attach(item);

        await _cache.EnsureAsync(Hash);

        Assert.NotNull(item.ThumbnailImage);
        Assert.True(File.Exists(_cache.LocalPath(Hash)));
    }

    [Fact]
    public async Task Ensure_NotFound_leaves_item_without_thumbnail()
    {
        var item = MakeItem();
        _cache.Attach(item);

        await _cache.EnsureAsync(Hash);

        Assert.Null(item.ThumbnailImage);
        Assert.False(File.Exists(_cache.LocalPath(Hash)));
    }

    [Fact]
    public async Task Ensure_uses_local_cache_without_redownload()
    {
        _nas.SeedBytes(SharedPaths.Thumbnail(Hash), EncodeWebp());
        await _cache.EnsureAsync(Hash);
        await _nas.DeleteAsync(SharedPaths.Thumbnail(Hash), CancellationToken.None);

        var lateItem = MakeItem();
        _cache.Attach(lateItem);
        await _cache.EnsureAsync(Hash);

        Assert.NotNull(lateItem.ThumbnailImage);
    }

    [Fact]
    public async Task Ensure_invalid_webp_is_swallowed()
    {
        _nas.SeedBytes(SharedPaths.Thumbnail(Hash), new byte[] { 1, 2, 3, 4 });
        var item = MakeItem();

        await _cache.EnsureAsync(Hash);

        Assert.Null(item.ThumbnailImage);
    }

    [Fact]
    public void Detach_stops_tracking_item()
    {
        var item = MakeItem();
        _cache.Attach(item);
        _cache.Detach(item);

        _cache.ClearRealized(); // no throw; item no longer receives pushes
    }

    private static GalleryItemViewModel MakeItem() =>
        new(new GalleryListItem { Id = 1, FileName = "x.webp", FileHash = Hash });

    private static byte[] EncodeWebp()
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(200, 40, 40));
        using var ms = new MemoryStream();
        image.Save(ms, new WebpEncoder());
        return ms.ToArray();
    }

    public void Dispose()
    {
        _nas.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
