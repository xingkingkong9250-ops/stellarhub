using System.IO;
using System.Windows.Media.Imaging;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.ViewModels;

namespace QL.World.Services.Nas;

/// <summary>
/// U6-A: downloads library thumbnails from the NAS
/// (<c>StellarHubShared/thumbnails/...</c>) into a per-OS-user cache and
/// pushes the decoded bitmap into realized gallery items. Also serves
/// pending-review thumbnails via <see cref="EnsurePendingAsync"/>.
/// Thread-safe; in-flight requests are deduplicated by hash.
/// </summary>
public sealed class SharedThumbnailCache : IGalleryThumbnailProvider
{
    private readonly INasConnector _connector;
    private readonly string _cacheDir;
    private readonly NetworkConditionMonitor? _networkMonitor;

    private readonly object _gate = new();
    private readonly Dictionary<string, GalleryItemViewModel> _realized = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    public SharedThumbnailCache(
        INasConnector connector,
        string cacheDirectory,
        NetworkConditionMonitor? networkMonitor = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _cacheDir = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        _networkMonitor = networkMonitor;
        Directory.CreateDirectory(_cacheDir);
    }

    // ─── IGalleryThumbnailProvider ─────────────────────────────────────

    public void Attach(GalleryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _realized[item.FileHash] = item;
        }

        if (IsWeakNetwork)
            return;

        _ = EnsureAsync(item.FileHash);
    }

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
    /// Ensures a library thumbnail is cached locally, then decodes and pushes
    /// it to every realized item with the same hash.
    /// </summary>
    public async Task EnsureAsync(string hash)
    {
        if (!IsWeakNetwork)
            await EnsureLocalAsync(hash, SharedPaths.Thumbnail(hash)).ConfigureAwait(false);
        PushIfRealized(hash);
    }

    /// <summary>
    /// U6-C: ensures a pending-review thumbnail for <paramref name="submitId"/>
    /// is cached and returns the decoded bitmap (null when absent/unreadable).
    /// </summary>
    public async Task<BitmapSource?> EnsurePendingAsync(string hash, string submitId)
    {
        await EnsureLocalAsync(hash, SharedPaths.PendingThumbnail(submitId))
            .ConfigureAwait(false);
        string localPath = LocalPath(hash);
        return File.Exists(localPath)
            ? WebpImageDecoder.DecodeFile(localPath)
            : null;
    }

    /// <summary>
    /// Downloads <paramref name="remotePath"/> into the cache when missing.
    /// In-flight requests are deduplicated; 404 and other failures leave no
    /// local file and never throw.
    /// </summary>
    private async Task EnsureLocalAsync(string hash, string remotePath)
    {
        string localPath = LocalPath(hash);

        lock (_gate)
        {
            if (File.Exists(localPath) || !_inFlight.Add(hash))
                return;
        }

        try
        {
            await _connector.DownloadFileAsync(remotePath, localPath, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            TryDelete(localPath);
        }
        catch
        {
            TryDelete(localPath);
        }
        finally
        {
            lock (_gate) { _inFlight.Remove(hash); }
        }
    }

    /// <summary>Decodes the cached file and pushes it into the realized item (if any).</summary>
    private void PushIfRealized(string hash)
    {
        string localPath = LocalPath(hash);
        if (!File.Exists(localPath)) return;

        BitmapSource? bitmap = WebpImageDecoder.DecodeFile(localPath);
        if (bitmap is null)
        {
            TryDelete(localPath);
            return;
        }

        lock (_gate)
        {
            if (_realized.TryGetValue(hash, out var item))
                item.ThumbnailImage = bitmap;
        }
    }

    /// <summary>Batch-prefetches thumbnails (used right after the shared library loads).</summary>
    public void Prefetch(IEnumerable<GalleryItemViewModel> items)
    {
        if (IsWeakNetwork) return;
        foreach (var item in items)
            _ = EnsureAsync(item.FileHash);
    }

    public string LocalPath(string hash) => Path.Combine(_cacheDir, $"{hash}.webp");

    private bool IsWeakNetwork => _networkMonitor is { IsWeakNetwork: true };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
