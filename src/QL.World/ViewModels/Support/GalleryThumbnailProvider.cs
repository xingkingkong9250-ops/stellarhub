using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.ViewModels.Support;

/// <summary>
/// UI-side thumbnail loader: requests webps from ThumbnailService, decodes
/// them with ImageSharp (WIC has no guaranteed WebP codec), caches frozen
/// BitmapSources (LRU), and pushes them into realized gallery items.
/// Lives in the ViewModel layer because it owns WPF image types;
/// ThumbnailService itself stays UI-free.
/// M5-F: handles two tiers — Thumb (256px, gallery grid/list) and Preview
/// (1024px, detail pane). The Thumb tier is cached in the in-memory LRU;
/// the Preview tier is decoded on demand and pushed to
/// <see cref="GalleryItemViewModel.PreviewImage"/> without caching (the
/// detail pane shows one item at a time).
/// </summary>
public sealed class GalleryThumbnailProvider : IDisposable
{
    private const int CACHE_LIMIT = 500;
    private const int PREFETCH_RANGE = 15;

    private readonly ThumbnailService _thumbnails;
    private readonly INasConnectorFactory? _nasFactory;
    private readonly AppSettingsService? _settings;
    private readonly NetworkConditionMonitor? _networkMonitor;
    private readonly object _gate = new();

    private readonly Dictionary<string, GalleryItemViewModel> _realized = new();
    private readonly Dictionary<string, BitmapSource> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
    private readonly HashSet<string> _inFlight = new();

    // M5-F: Preview-tier tracking. Separate from _realized because the detail
    // item may not be in the visible gallery range.
    private readonly Dictionary<string, GalleryItemViewModel> _previewRequested = new();
    private readonly HashSet<string> _previewInFlight = new();

    private readonly Dictionary<int, INasConnector> _connectors = new();
    private readonly object _connectorGate = new();
    private bool _disposed;

    /// <summary>
    /// M5-E: <paramref name="nasFactory"/> + <paramref name="settings"/> let
    /// the provider build <see cref="RemoteImageSource"/> instances for
    /// gallery rows whose <see cref="GalleryItemViewModel.Source"/> is
    /// <see cref="ImageSource.Remote"/>. Connectors are cached per
    /// <see cref="NasConnectionSpec.Id"/> so the auth handshake + HttpClient
    /// pool survive across many thumbnails instead of being rebuilt per row.
    /// Defaults are for tests that only exercise the local path.
    /// </summary>
    public GalleryThumbnailProvider(
        ThumbnailService thumbnails,
        INasConnectorFactory? nasFactory = null,
        AppSettingsService? settings = null,
        NetworkConditionMonitor? networkMonitor = null)
    {
        _thumbnails = thumbnails;
        _nasFactory = nasFactory;
        _settings = settings;
        _networkMonitor = networkMonitor;
        _thumbnails.ThumbnailReady += OnThumbnailReady;
    }

    /// <summary>Called by the attached behavior when an item's container loads.</summary>
    public void Attach(GalleryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _realized[item.FileHash] = item;
            if (_cache.TryGetValue(item.FileHash, out BitmapSource? cached))
            {
                TouchLru(item.FileHash);
                item.ThumbnailImage = cached;
                return;
            }
        }

        if (_thumbnails.ThumbnailExists(item.FileHash, ThumbnailLevel.Thumb))
        {
            StartLoad(item);
        }
        else
        {
            // M5-H: on weak networks, skip remote thumbnail requests — only
            // show cached thumbs so the gallery stays responsive over a slow
            // link. Local items are unaffected.
            bool isRemote = item.Source == QL.World.Models.ImageSource.Remote;
            if (isRemote && _networkMonitor is { IsWeakNetwork: true })
                return;

            _thumbnails.Request(item.FileHash, CreateSource(item),
                ThumbnailLevel.Thumb, highPriority: true);
        }
    }

    /// <summary>Called when the container is recycled/unloaded.</summary>
    public void Detach(GalleryItemViewModel item)
    {
        lock (_gate)
        {
            _realized.Remove(item.FileHash);
        }
    }

    public void ClearRealized()
    {
        lock (_gate)
        {
            _realized.Clear();
        }
    }

    /// <summary>
    /// M5-H: prefetches thumbnails for items surrounding the visible range.
    /// Called by <see cref="GalleryViewModel"/> after a page loads so the
    /// next scroll batch is already on disk. Items whose thumb already
    /// exists are silently skipped (the service also checks). Remote items
    /// are skipped on weak networks (same gate as <see cref="Attach"/>).
    /// </summary>
    public void Prefetch(IEnumerable<GalleryItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var toEnqueue = new List<(string Hash, IImageSource Source)>();
        foreach (GalleryItemViewModel item in items)
        {
            bool isRemote = item.Source == QL.World.Models.ImageSource.Remote;
            if (isRemote && _networkMonitor is { IsWeakNetwork: true })
                continue;
            toEnqueue.Add((item.FileHash, CreateSource(item)));
        }
        if (toEnqueue.Count > 0)
            _thumbnails.RequestPrefetch(toEnqueue);
    }

    /// <summary>
    /// M5-F: requests the Preview-tier (1024px) WebP for the detail pane.
    /// When the WebP is ready, <see cref="OnThumbnailReady"/> decodes it and
    /// pushes the <see cref="BitmapSource"/> to <see cref="GalleryItemViewModel.PreviewImage"/>.
    /// If the Preview WebP already exists on disk, loads it immediately.
    /// </summary>
    public void RequestPreview(GalleryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _previewRequested[item.FileHash] = item;
        }

        if (_thumbnails.ThumbnailExists(item.FileHash, ThumbnailLevel.Preview))
        {
            StartPreviewLoad(item);
        }
        else
        {
            _thumbnails.Request(item.FileHash, CreateSource(item),
                ThumbnailLevel.Preview, highPriority: true);
        }
    }

    /// <summary>
    /// M5-F: clears the preview request so a stale callback doesn't update
    /// an item that's no longer in the detail pane.
    /// </summary>
    public void ClearPreviewRequest(string hash)
    {
        lock (_gate)
        {
            _previewRequested.Remove(hash);
        }
    }

    /// <summary>
    /// M5-E: force-regenerate the thumbnail for <paramref name="item"/>.
    /// Called by the B2-3-ACT "重新生成缩略图" context action. Builds the
    /// same <see cref="IImageSource"/> as <see cref="Attach"/> so remote rows
    /// reuse the cached <see cref="INasConnector"/> + RemoteImageSource path
    /// instead of being skipped (which would leave a stale WebP on disk).
    /// </summary>
    public void Regenerate(GalleryItemViewModel item)
    {
        _thumbnails.Regenerate(item.FileHash, CreateSource(item), ThumbnailLevel.Thumb);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thumbnails.ThumbnailReady -= OnThumbnailReady;
        // M5-E: release cached NAS connectors (HttpClient pools) on shutdown.
        lock (_connectorGate)
        {
            foreach (INasConnector c in _connectors.Values)
            {
                try { (c as IDisposable)?.Dispose(); }
                catch { /* best effort */ }
            }
            _connectors.Clear();
        }
    }

    /// <summary>
    /// M5-F: routes the ThumbnailReady callback by level. Thumb → load into
    /// the gallery grid/list; Preview → load into the detail pane.
    /// </summary>
    private void OnThumbnailReady(string hash, ThumbnailLevel level)
    {
        if (_disposed) return;

        GalleryItemViewModel? item;
        lock (_gate)
        {
            if (level == ThumbnailLevel.Thumb)
            {
                if (!_realized.TryGetValue(hash, out item))
                    return;
            }
            else
            {
                if (!_previewRequested.TryGetValue(hash, out item))
                    return;
            }
        }

        if (level == ThumbnailLevel.Preview)
            StartPreviewLoad(item);
        else
            StartLoad(item);
    }

    /// <summary>
    /// M5-E: builds the <see cref="IImageSource"/> matching this row. Local
    /// rows wrap <see cref="GalleryItemViewModel.FilePath"/> in a
    /// <see cref="LocalImageSource"/>; remote rows look up the cached
    /// <see cref="INasConnector"/> by <see cref="GalleryItemViewModel.NasConnectionId"/>
    /// and strip the <c>nas://{id}</c> scheme from the stored
    /// <c>file_path</c> to recover the remote path. Falls back to a local
    /// source when the connection was deleted from settings (the thumbnail
    /// will fail to decode and the placeholder is copied — same UX as a
    /// missing local file).
    /// </summary>
    private IImageSource CreateSource(GalleryItemViewModel item)
    {
        if (item.Source == QL.World.Models.ImageSource.Remote &&
            item.NasConnectionId is int connId &&
            _nasFactory is not null &&
            GetConnector(connId) is { } connector)
        {
            string remotePath = StripNasPrefix(item.FilePath, connId);
            return new RemoteImageSource(connector, remotePath, item.FilePath);
        }
        return new LocalImageSource(item.FilePath);
    }

    /// <summary>
    /// Lazily builds + caches a connector for the given connection id. Returns
    /// null when the id no longer exists in <see cref="AppSettingsService.NasConnections"/>
    /// (user deleted it after the scan). Cache is keyed by id so a single
    /// HttpClient/auth handshake serves the whole gallery session.
    /// </summary>
    private INasConnector? GetConnector(int connId)
    {
        if (_settings is null || _nasFactory is null)
            return null;
        lock (_connectorGate)
        {
            if (_connectors.TryGetValue(connId, out INasConnector? cached))
                return cached;
            NasConnectionSpec? spec = _settings.NasConnections
                .FirstOrDefault(c => c.Id == connId);
            if (spec is null)
                return null;
            INasConnector connector = _nasFactory.Create(spec, plaintextPassword: null);
            _connectors[connId] = connector;
            return connector;
        }
    }

    /// <summary>
    /// Strips the <c>nas://{connId}</c> scheme prefix from a stored canonical
    /// path to recover the remote path rooted at
    /// <see cref="NasConnectionSpec.RootPath"/>. Mirrors
    /// <see cref="RemoteFileSystemAccessor"/>'s private <c>ToRemote</c>.
    /// Falls back to the raw string when it isn't a canonical nas:// path.
    /// </summary>
    private static string StripNasPrefix(string canonical, int connId)
    {
        string prefix = $"nas://{connId}";
        if (!canonical.StartsWith(prefix, StringComparison.Ordinal))
            return canonical;
        string rest = canonical[prefix.Length..];
        return string.IsNullOrEmpty(rest) ? "/" : rest;
    }

    private void StartLoad(GalleryItemViewModel item)
    {
        string path = _thumbnails.GetThumbnailPath(item.FileHash, ThumbnailLevel.Thumb);

        lock (_gate)
        {
            if (_inFlight.Contains(item.FileHash)) return;
            _inFlight.Add(item.FileHash);
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) return;
                BitmapSource? bitmap = DecodeWebp(path);
                if (bitmap is null) return;

                lock (_gate)
                {
                    PutCache(item.FileHash, bitmap);
                    if (_realized.TryGetValue(item.FileHash, out GalleryItemViewModel? live))
                        live.ThumbnailImage = bitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ThumbnailUI] decode failed for {item.FileHash}: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight.Remove(item.FileHash);
                }
            }
        });
    }

    /// <summary>
    /// M5-F: decodes the Preview-tier WebP (1024px) and pushes the frozen
    /// BitmapSource to <see cref="GalleryItemViewModel.PreviewImage"/>.
    /// Not cached in the in-memory LRU — the detail pane shows one item at
    /// a time and the on-disk WebP is the cache.
    /// </summary>
    private void StartPreviewLoad(GalleryItemViewModel item)
    {
        string path = _thumbnails.GetThumbnailPath(item.FileHash, ThumbnailLevel.Preview);

        lock (_gate)
        {
            if (_previewInFlight.Contains(item.FileHash)) return;
            _previewInFlight.Add(item.FileHash);
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) return;
                BitmapSource? bitmap = DecodeWebp(path);
                if (bitmap is null) return;

                lock (_gate)
                {
                    if (_previewRequested.TryGetValue(item.FileHash,
                        out GalleryItemViewModel? live))
                    {
                        live.PreviewImage = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ThumbnailUI] preview decode failed for {item.FileHash}: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _previewInFlight.Remove(item.FileHash);
                }
            }
        });
    }

    private static BitmapSource? DecodeWebp(string path)
    {
        byte[] rgba;
        int width;
        int height;

        using (SixLabors.ImageSharp.Image<Rgba32> image =
               SixLabors.ImageSharp.Image.Load<Rgba32>(path))
        {
            width = image.Width;
            height = image.Height;
            rgba = new byte[width * height * 4];
            image.CopyPixelDataTo(rgba);
        }

        // R,G,B,A → B,G,R,A for PixelFormats.Bgra32.
        for (int i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void TouchLru(string key)
    {
        if (_lruNodes.TryGetValue(key, out LinkedListNode<string>? node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private void PutCache(string key, BitmapSource bitmap)
    {
        if (_lruNodes.TryGetValue(key, out LinkedListNode<string>? existing))
        {
            _lru.Remove(existing);
            _lruNodes.Remove(key);
        }

        _cache[key] = bitmap;
        LinkedListNode<string> added = _lru.AddFirst(key);
        _lruNodes[key] = added;

        while (_cache.Count > CACHE_LIMIT && _lru.Last is not null)
        {
            string oldest = _lru.Last.Value;
            _lru.RemoveLast();
            _lruNodes.Remove(oldest);
            _cache.Remove(oldest);
        }
    }
}
