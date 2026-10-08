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
public sealed class GalleryThumbnailProvider : IGalleryThumbnailProvider, IDisposable
{
    private const int CACHE_LIMIT = 500;
    private const int PREFETCH_RANGE = 15;
    private const int LOAD_RETRY_DELAY_MS = 350;

    private readonly ThumbnailService _thumbnails;
    private readonly INasConnectorFactory? _nasFactory;
    private readonly AppSettingsService? _settings;
    private readonly NetworkConditionMonitor? _networkMonitor;
    private readonly object _gate = new();

    // F-08: realized rows are tracked by their unique DB row Id (not FileHash):
    // identical images share a hash, and a hash-keyed map let duplicate rows
    // overwrite each other — the loser's card stayed blank forever. The hash
    // index fans a ready bitmap out to every visible row sharing that hash.
    private readonly Dictionary<long, GalleryItemViewModel> _realized = new();
    private readonly Dictionary<string, HashSet<long>> _realizedHashes = new();
    private readonly Dictionary<string, BitmapSource> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
    private readonly HashSet<string> _inFlight = new();
    // F-08: per-hash transient-load retry count (decoder race / AV lock).
    private readonly Dictionary<string, int> _loadAttempts = new();
    // F-08: hashes whose corrupt WebP already triggered one regeneration,
    // so a genuinely undecodable file doesn't enqueue jobs in a tight loop.
    private readonly HashSet<string> _regenerationRequested = new();
    private BitmapSource? _placeholderBitmap;

    // M5-F: Preview-tier tracking. Separate from _realized because the detail
    // item may not be in the visible gallery range.
    private readonly Dictionary<long, GalleryItemViewModel> _previewRequested = new();
    private readonly Dictionary<string, long> _previewHashToId = new();
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
        // F-08: when a weak network recovers, re-request thumbnails for the
        // remote rows that Attach had to skip — otherwise those cards stay
        // blank until the user scrolls them out and back again.
        if (_networkMonitor is not null)
            _networkMonitor.Changed += OnNetworkConditionChanged;
    }

    /// <summary>Called by the attached behavior when an item's container loads.</summary>
    public void Attach(GalleryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            // Id-keyed: duplicate images (same FileHash) no longer evict each
            // other from the realized set.
            _realized[item.Id] = item;
            if (!_realizedHashes.TryGetValue(item.FileHash, out HashSet<long>? ids))
            {
                ids = new HashSet<long>();
                _realizedHashes[item.FileHash] = ids;
            }
            ids.Add(item.Id);

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
            // link. Local items are unaffected. The Changed handler re-runs
            // Attach once the network recovers (F-08).
            if (IsRemoteOnWeakNetwork(item))
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
            // F-08: identity check — with container recycling, an Unloaded for
            // the old row can race a DataContextChanged Attach for the new row
            // sharing the same visual container. Never evict a different row.
            if (!_realized.TryGetValue(item.Id, out GalleryItemViewModel? current) ||
                !ReferenceEquals(current, item))
            {
                return;
            }
            _realized.Remove(item.Id);
            if (_realizedHashes.TryGetValue(item.FileHash, out HashSet<long>? ids))
            {
                ids.Remove(item.Id);
                if (ids.Count == 0)
                    _realizedHashes.Remove(item.FileHash);
            }
        }
    }

    public void ClearRealized()
    {
        lock (_gate)
        {
            _realized.Clear();
            _realizedHashes.Clear();
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
            _previewRequested[item.Id] = item;
            _previewHashToId[item.FileHash] = item.Id;
        }

        if (_thumbnails.ThumbnailExists(item.FileHash, ThumbnailLevel.Preview))
        {
            StartPreviewLoad(item);
        }
        else
        {
            if (IsRemoteOnWeakNetwork(item))
                return;
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
            if (_previewHashToId.TryGetValue(hash, out long id))
            {
                _previewRequested.Remove(id);
                _previewHashToId.Remove(hash);
            }
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
        if (_networkMonitor is not null)
            _networkMonitor.Changed -= OnNetworkConditionChanged;
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
    /// F-08: network health flipped. When the link is healthy again, re-run
    /// Attach for every realized remote row so skipped thumbnails arrive
    /// without requiring a scroll round-trip.
    /// </summary>
    private void OnNetworkConditionChanged()
    {
        if (_disposed || _networkMonitor is null || _networkMonitor.IsWeakNetwork)
            return;

        List<GalleryItemViewModel> snapshot;
        lock (_gate)
        {
            snapshot = _realized.Values
                .Where(i => i.Source == QL.World.Models.ImageSource.Remote)
                .ToList();
        }
        foreach (GalleryItemViewModel item in snapshot)
            Attach(item);
    }

    private bool IsRemoteOnWeakNetwork(GalleryItemViewModel item) =>
        item.Source == QL.World.Models.ImageSource.Remote &&
        _networkMonitor is { IsWeakNetwork: true };

    /// <summary>
    /// M5-F: routes the ThumbnailReady callback by level. Thumb → load into
    /// the gallery grid/list (all visible rows sharing the hash); Preview →
    /// load into the detail pane.
    /// </summary>
    private void OnThumbnailReady(string hash, ThumbnailLevel level)
    {
        if (_disposed) return;

        List<GalleryItemViewModel> targets = [];
        lock (_gate)
        {
            if (level == ThumbnailLevel.Thumb)
            {
                if (_realizedHashes.TryGetValue(hash, out HashSet<long>? ids))
                {
                    foreach (long id in ids)
                    {
                        if (_realized.TryGetValue(id, out GalleryItemViewModel? row))
                            targets.Add(row);
                    }
                }
            }
            else if (_previewHashToId.TryGetValue(hash, out long previewId) &&
                     _previewRequested.TryGetValue(previewId, out GalleryItemViewModel? previewItem))
            {
                targets.Add(previewItem);
            }
        }

        if (targets.Count == 0)
            return;

        if (level == ThumbnailLevel.Preview)
            StartPreviewLoad(targets[0]);
        else
            foreach (GalleryItemViewModel item in targets)
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
        string hash = item.FileHash;
        string path = _thumbnails.GetThumbnailPath(hash, ThumbnailLevel.Thumb);

        lock (_gate)
        {
            if (_inFlight.Contains(hash)) return;
            _inFlight.Add(hash);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // F-08, case 1: the WebP vanished (regeneration deleted it,
                // placeholder copy lost a race). Ask the service to produce it
                // again; its ThumbnailReady callback re-enters StartLoad.
                if (!File.Exists(path))
                {
                    _thumbnails.Request(hash, CreateSource(item),
                        ThumbnailLevel.Thumb, highPriority: true);
                    return;
                }

                BitmapSource? bitmap = Helpers.WebpImageDecoder.DecodeFile(path);

                // F-08, case 2: transient decode failure (AV scan holding the
                // file, disk hiccup). Retry once after a short delay before
                // treating the WebP as corrupt — previously the card stayed
                // blank with no recovery path.
                if (bitmap is null)
                {
                    int attempt;
                    lock (_gate)
                    {
                        attempt = _loadAttempts.TryGetValue(hash, out int n) ? n + 1 : 1;
                        _loadAttempts[hash] = attempt;
                    }
                    if (attempt == 1)
                    {
                        await Task.Delay(LOAD_RETRY_DELAY_MS).ConfigureAwait(false);
                        if (!_disposed && File.Exists(path))
                            bitmap = Helpers.WebpImageDecoder.DecodeFile(path);
                    }
                }

                if (bitmap is not null)
                {
                    lock (_gate)
                    {
                        _loadAttempts.Remove(hash);
                        PutCache(hash, bitmap);
                        PublishToRealized(hash, bitmap);
                    }
                    return;
                }

                // F-08, case 3: the on-disk WebP is genuinely unreadable.
                // Never leave a permanent blank card — show the placeholder
                // immediately, then request one bounded regeneration (a
                // missing source file will publish a placeholder via the
                // service, which is the correct end state for that row).
                QL.World.App.WriteLog(
                    $"[THUMB] decode returned null hash={hash} path={path} exists={File.Exists(path)}");
                BitmapSource? fallback = GetPlaceholderBitmap();
                if (fallback is not null)
                {
                    lock (_gate)
                        PublishToRealized(hash, fallback);
                }

                bool shouldRegenerate;
                lock (_gate)
                {
                    shouldRegenerate = _regenerationRequested.Add(hash);
                }
                if (shouldRegenerate)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ThumbnailUI] corrupt WebP for {hash}; requesting regeneration");
                    _thumbnails.Regenerate(hash, CreateSource(item), ThumbnailLevel.Thumb);
                }
            }
            catch (Exception ex)
            {
                QL.World.App.WriteLog($"[THUMB] StartLoad exception hash={hash}: {ex.GetType().Name} {ex.Message}");
                System.Diagnostics.Debug.WriteLine(
                    $"[ThumbnailUI] decode failed for {hash}: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight.Remove(hash);
                }
            }
        });
    }

    /// <summary>Pushes a bitmap to every currently-realized row sharing the hash.</summary>
    private int PublishToRealized(string hash, BitmapSource bitmap)
    {
        int n = 0;
        if (_realizedHashes.TryGetValue(hash, out HashSet<long>? ids))
        {
            foreach (long id in ids)
            {
                if (_realized.TryGetValue(id, out GalleryItemViewModel? row))
                {
                    row.ThumbnailImage = bitmap;
                    n++;
                }
            }
        }
        return n;
    }

    /// <summary>
    /// F-08: decodes the service's placeholder WebP once and caches the
    /// frozen bitmap so failed/corrupt rows always show the checker tile
    /// instead of an empty card.
    /// </summary>
    private BitmapSource? GetPlaceholderBitmap()
    {
        lock (_gate)
        {
            if (_placeholderBitmap is not null)
                return _placeholderBitmap;
        }

        try
        {
            string placeholderPath = _thumbnails.GetPlaceholderPath();
            if (!File.Exists(placeholderPath))
                return null;
            if (Helpers.WebpImageDecoder.DecodeFile(placeholderPath) is not { } decoded)
                return null;
            lock (_gate)
            {
                _placeholderBitmap ??= decoded;
            }
            return _placeholderBitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// M5-F: decodes the Preview-tier WebP (1024px) and pushes the frozen
    /// BitmapSource to <see cref="GalleryItemViewModel.PreviewImage"/>.
    /// Not cached in the in-memory LRU — the detail pane shows one item at
    /// a time and the on-disk WebP is the cache.
    /// </summary>
    private void StartPreviewLoad(GalleryItemViewModel item)
    {
        string hash = item.FileHash;
        string path = _thumbnails.GetThumbnailPath(hash, ThumbnailLevel.Preview);

        lock (_gate)
        {
            if (_previewInFlight.Contains(hash)) return;
            _previewInFlight.Add(hash);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    // F-08: file vanished (regeneration race) — re-request;
                    // ThumbnailReady re-enters this path.
                    _thumbnails.Request(hash, CreateSource(item),
                        ThumbnailLevel.Preview, highPriority: true);
                    return;
                }
                BitmapSource? bitmap = Helpers.WebpImageDecoder.DecodeFile(path);
                if (bitmap is null)
                {
                    // F-08: one transient-failure retry, same as the Thumb tier.
                    await Task.Delay(LOAD_RETRY_DELAY_MS).ConfigureAwait(false);
                    if (!_disposed && File.Exists(path))
                        bitmap = Helpers.WebpImageDecoder.DecodeFile(path);
                }
                if (bitmap is null)
                    return;

                lock (_gate)
                {
                    if (_previewHashToId.TryGetValue(hash, out long liveId) &&
                        _previewRequested.TryGetValue(liveId, out GalleryItemViewModel? live))
                    {
                        live.PreviewImage = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ThumbnailUI] preview decode failed for {hash}: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _previewInFlight.Remove(hash);
                }
            }
        });
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
