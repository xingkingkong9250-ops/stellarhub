using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QL.World.Services;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.ViewModels.Support;

/// <summary>
/// UI-side thumbnail loader: requests webps from ThumbnailService, decodes
/// them with ImageSharp (WIC has no guaranteed WebP codec), caches frozen
/// BitmapSources (LRU), and pushes them into realized gallery items.
/// Lives in the ViewModel layer because it owns WPF image types;
/// ThumbnailService itself stays UI-free.
/// </summary>
public sealed class GalleryThumbnailProvider : IDisposable
{
    private const int CACHE_LIMIT = 500;

    private readonly ThumbnailService _thumbnails;
    private readonly object _gate = new();

    private readonly Dictionary<string, GalleryItemViewModel> _realized = new();
    private readonly Dictionary<string, BitmapSource> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
    private readonly HashSet<string> _inFlight = new();
    private bool _disposed;

    public GalleryThumbnailProvider(ThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
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

        if (_thumbnails.ThumbnailExists(item.FileHash))
        {
            StartLoad(item);
        }
        else
        {
            _thumbnails.Request(item.FileHash, item.FilePath, highPriority: true);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thumbnails.ThumbnailReady -= OnThumbnailReady;
    }

    private void OnThumbnailReady(string hash)
    {
        if (_disposed) return;

        GalleryItemViewModel? item;
        lock (_gate)
        {
            if (!_realized.TryGetValue(hash, out item))
                return;
        }
        StartLoad(item);
    }

    private void StartLoad(GalleryItemViewModel item)
    {
        string path = _thumbnails.GetThumbnailPath(item.FileHash);

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
