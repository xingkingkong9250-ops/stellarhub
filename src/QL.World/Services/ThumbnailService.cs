using System.IO;
using QL.World.Helpers;
using QL.World.Services.Nas;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QL.World.Services;

/// <summary>
/// Generates WebP thumbnails on bounded-concurrency background workers.
/// Visible items can be promoted with <see cref="Prioritize"/>.
/// M5-F output layout: thumbnails\&lt;hash2&gt;\&lt;hash&gt;_thumb.webp (256px)
/// and &lt;hash&gt;_preview.webp (1024px). Legacy &lt;hash&gt;.webp files are
/// migrated to _thumb on startup.
/// This service has no UI dependency — consumers get file paths via
/// <see cref="ThumbnailReady"/> and load bitmaps themselves.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int WORKER_COUNT = 2;
    private const int PLACEHOLDER_SIZE = 64;
    private const int PREVIEW_SIZE = 1024;

    private readonly string _root;
    private readonly string _tempRoot;
    private readonly AppSettingsService _settings;
    private readonly IMagickDecoder _magick;

    private readonly object _queueGate = new();
    private readonly PriorityQueue<string, long> _queue = new();
    private readonly Dictionary<string, Job> _jobs = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;

    private long _normalOrder;
    private long _priorityOrder = 1_000_000_000;
    private bool _disposed;
    private Task? _placeholderTask;

    public ThumbnailService(string dataRoot, AppSettingsService settings, IMagickDecoder? magick = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.Combine(dataRoot, AppDataRootResolver.ThumbnailsFolderName);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _magick = magick ?? new MagickDecoder();
        Directory.CreateDirectory(_root);
        // M5-E: remote (NAS) thumbnails download the full original into a
        // per-hash temp file before decoding. %TEMP%\StellarHub\ matches the
        // U5 brand migration; named by hash so two workers never collide.
        _tempRoot = Path.Combine(Path.GetTempPath(), "StellarHub");

        // M5-F: migrate legacy <hash>.webp → <hash>_thumb.webp (idempotent).
        MigrateLegacyThumbnails();

        _workers = Enumerable.Range(0, WORKER_COUNT)
            .Select(_ => Task.Run(ConsumeLoop))
            .ToArray();

        _placeholderTask = Task.Run(EnsurePlaceholderOnDisk);
    }

    /// <summary>
    /// M5-F: Raised (on a worker thread) when a thumbnail becomes available.
    /// The <see cref="ThumbnailLevel"/> tells the consumer which tier was
    /// generated so it can update the matching layer in ProgressiveImage.
    /// </summary>
    public event Action<string, ThumbnailLevel>? ThumbnailReady;

    public int PendingCount
    {
        get { lock (_queueGate) return _queue.Count; }
    }

    /// <summary>
    /// M5-F: Returns the on-disk path for the given hash + level.
    /// Layout: thumbnails\&lt;hash2&gt;\&lt;hash&gt;_&lt;level&gt;.webp.
    /// </summary>
    public string GetThumbnailPath(string hash, ThumbnailLevel level = ThumbnailLevel.Thumb)
    {
        string key = NormalizeHash(hash);
        return Path.Combine(_root, key[..2], $"{key}_{LevelSuffix(level)}.webp");
    }

    public bool ThumbnailExists(string hash, ThumbnailLevel level = ThumbnailLevel.Thumb) =>
        File.Exists(GetThumbnailPath(hash, level));

    /// <summary>
    /// Enqueues generation unless the WebP already exists.
    /// M5-F: <paramref name="level"/> selects the target tier (Thumb=256px,
    /// Preview=1024px). The same hash can have independent Thumb and Preview
    /// jobs in flight simultaneously.
    /// </summary>
    /// <param name="source">M5-E: replaces the <c>string sourcePath</c> overload
    /// so the same worker handles local files (<see cref="LocalImageSource"/>)
    /// and remote NAS bytes (<see cref="RemoteImageSource"/>).</param>
    public void Request(string hash, IImageSource source,
        ThumbnailLevel level = ThumbnailLevel.Thumb,
        bool highPriority = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        string key = NormalizeHash(hash);
        if (ThumbnailExists(key, level))
            return;

        string composite = CompositeKey(key, level);
        lock (_queueGate)
        {
            if (_jobs.ContainsKey(composite))
                return;

            long order = highPriority ? --_priorityOrder : --_normalOrder;
            var job = new Job(key, source, level, order);
            _jobs[composite] = job;
            _queue.Enqueue(composite, order);
        }
        _signal.Set();
    }

    /// <summary>Promotes a queued job ahead of normal (previously enqueued) jobs.</summary>
    public void Prioritize(string hash, ThumbnailLevel level = ThumbnailLevel.Thumb)
    {
        string key = NormalizeHash(hash);
        string composite = CompositeKey(key, level);
        lock (_queueGate)
        {
            if (!_jobs.TryGetValue(composite, out Job? job) || job.Running)
                return;

            job.Order = --_priorityOrder;
            // Re-enqueue with the new priority; stale dequeues are guarded
            // by comparing the stored order.
            _queue.Enqueue(composite, job.Order);
        }
        _signal.Set();
    }

    /// <summary>Forces regeneration even when a WebP already exists.</summary>
    public void Regenerate(string hash, IImageSource source,
        ThumbnailLevel level = ThumbnailLevel.Thumb)
    {
        ArgumentNullException.ThrowIfNull(source);
        string key = NormalizeHash(hash);
        try { File.Delete(GetThumbnailPath(key, level)); }
        catch (IOException) { /* Will be overwritten on save. */ }

        string composite = CompositeKey(key, level);
        lock (_queueGate)
        {
            _jobs.Remove(composite);
        }
        Request(key, source, level, highPriority: true);
    }

    public Task WaitPlaceholderAsync() => _placeholderTask ?? Task.CompletedTask;

    /// <summary>
    /// M5-H: prefetches thumbnails for items surrounding the visible range.
    /// Each item is enqueued at normal (low) priority so visible items
    /// (enqueued via <see cref="Request"/> with highPriority=true) always
    /// jump ahead. Items whose thumb WebP already exists are silently skipped.
    /// </summary>
    public void RequestPrefetch(IEnumerable<(string Hash, IImageSource Source)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach ((string hash, IImageSource source) in items)
        {
            if (string.IsNullOrWhiteSpace(hash))
                continue;
            string key = NormalizeHash(hash);
            if (ThumbnailExists(key, ThumbnailLevel.Thumb))
                continue;
            Request(key, source, ThumbnailLevel.Thumb, highPriority: false);
        }
    }

    private void ConsumeLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            string? composite = null;
            Job? job = null;

            lock (_queueGate)
            {
                while (_queue.Count > 0)
                {
                    if (!_queue.TryPeek(out string? candidate, out long order))
                        break;
                    _queue.Dequeue();
                    if (_jobs.TryGetValue(candidate, out Job? queued) &&
                        !queued.Running && queued.Order == order)
                    {
                        composite = candidate;
                        job = queued;
                        job.Running = true;
                        break;
                    }
                }
            }

            if (composite is null || job is null)
            {
                try { _signal.WaitOne(500, exitContext: false); }
                catch (ObjectDisposedException) { return; }
                continue;
            }

            try
            {
                ProcessJob(job);
            }
            catch (Exception ex) when (ex is not (ObjectDisposedException))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Thumbnail] {job.Hash} {job.Level} failed: {ex.Message}");
                CopyPlaceholderFor(job.Hash, job.Level);
            }

            lock (_queueGate)
            {
                _jobs.Remove(composite);
            }
            ThumbnailReady?.Invoke(job.Hash, job.Level);
        }
    }

    private void ProcessJob(Job job)
    {
        string output = GetThumbnailPath(job.Hash, job.Level);
        if (File.Exists(output))
            return;

        // M5-E: local sources take the fast path (Image.Load(path) + Magick
        // fallback for tga/exr/hdr/psd). Remote sources stream the full
        // original into %TEMP%\StellarHub\<hash>.<guid>.tmp via
        // IImageSource.OpenReadAsync, decode that temp file, save WebP, then
        // delete the temp in finally (M5 simple path; M6+ adds Range partial
        // decode so only the bytes a 256px thumb needs cross the wire).
        string? sourcePath = job.Source.LocalPath;
        string? tempFile = null;
        try
        {
            if (sourcePath is null)
            {
                Directory.CreateDirectory(_tempRoot);
                tempFile = Path.Combine(_tempRoot, $"{job.Hash}.{Guid.NewGuid():N}.tmp");
                using (Stream src = job.Source.OpenReadAsync(_shutdown.Token).GetAwaiter().GetResult())
                using (var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write,
                               FileShare.None, 81920, useAsync: false))
                {
                    src.CopyTo(dst, 81920);
                }
                sourcePath = tempFile;
            }

            if (!File.Exists(sourcePath))
            {
                CopyPlaceholderFor(job.Hash, job.Level);
                return;
            }

            // M5-F: target size depends on level. Thumb follows the user's
            // ThumbnailSize setting (128–512, default 256); Preview is fixed
            // at 1024 for the detail pane.
            int size = job.Level == ThumbnailLevel.Preview
                ? PREVIEW_SIZE
                : _settings.ThumbnailSize;
            int quality = _settings.ThumbnailQuality;

            using Image image = LoadDecoded(sourcePath);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Max,
            }));

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string temporary = output + $".{Guid.NewGuid():N}.partial";
            try
            {
                image.Save(temporary, new WebpEncoder { Quality = quality });
                File.Move(temporary, output, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            if (tempFile is not null)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                catch (IOException ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Thumbnail] failed to delete remote temp {tempFile}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// ImageSharp first; when it does not recognize the container
    /// (tga/exr/hdr/psd) fall back to the Magick decoder. Rethrows as
    /// <see cref="UnknownImageFormatException"/> when both fail.
    /// </summary>
    private Image LoadDecoded(string path)
    {
        try
        {
            return Image.Load(path);
        }
        catch (UnknownImageFormatException)
        {
            if (_magick.TryLoadFrame(path, out Image? frame) && frame is not null)
                return frame;
            throw;
        }
    }

    private string EnsurePlaceholderOnDisk()
    {
        string path = Path.Combine(_root, "placeholder.webp");
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(_root);
        using var image = new Image<Rgba32>(PLACEHOLDER_SIZE, PLACEHOLDER_SIZE);
        for (int y = 0; y < PLACEHOLDER_SIZE; y++)
        {
            for (int x = 0; x < PLACEHOLDER_SIZE; x++)
            {
                // Subtle checker so the placeholder is visibly "no image".
                bool dark = ((x / 8) + (y / 8)) % 2 == 0;
                byte v = dark ? (byte)54 : (byte)64;
                image[x, y] = new Rgba32(v, v, v + 4);
            }
        }

        string temporary = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            image.Save(temporary, new WebpEncoder { Quality = 70 });
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another worker/thread published the placeholder first.
        }
        finally
        {
            TryDelete(temporary);
        }
        return path;
    }

    private void CopyPlaceholderFor(string hash, ThumbnailLevel level)
    {
        try
        {
            string placeholder = EnsurePlaceholderOnDisk();
            string output = GetThumbnailPath(hash, level);
            if (File.Exists(output))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(placeholder, output, overwrite: true);
        }
        catch
        {
            // Placeholder is best effort; UI falls back to its own empty state.
        }
    }

    /// <summary>
    /// M5-F: renames legacy &lt;hash&gt;.webp to &lt;hash&gt;_thumb.webp.
    /// Skips files that already have a _thumb/_preview suffix or are the
    /// placeholder. Idempotent: safe to call on every startup.
    /// </summary>
    private void MigrateLegacyThumbnails()
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(_root))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.webp"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    // Already migrated or special file.
                    if (name.EndsWith("_thumb", StringComparison.Ordinal) ||
                        name.EndsWith("_preview", StringComparison.Ordinal) ||
                        name == "placeholder")
                        continue;

                    string target = Path.Combine(
                        Path.GetDirectoryName(file)!,
                        $"{name}_thumb.webp");
                    if (File.Exists(target))
                    {
                        // Target already exists (e.g. previous partial migration);
                        // remove the legacy file to avoid confusion.
                        TryDelete(file);
                        continue;
                    }
                    try { File.Move(file, target); }
                    catch (IOException) { /* Best effort; next startup retries. */ }
                }
            }
        }
        catch (DirectoryNotFoundException) { /* No thumbnails yet — fine. */ }
        catch (IOException) { /* Best effort; non-fatal. */ }
    }

    private static string LevelSuffix(ThumbnailLevel level) => level switch
    {
        ThumbnailLevel.Thumb => "thumb",
        ThumbnailLevel.Preview => "preview",
        _ => "thumb",
    };

    private static string CompositeKey(string hash, ThumbnailLevel level) =>
        $"{hash}:{(int)level}";

    private static string NormalizeHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length < 2)
            throw new ArgumentException("Image hash is too short.", nameof(hash));
        return hash.ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* Best effort. */ }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _signal.Set();
        try
        {
            Task.WaitAll(_workers, TimeSpan.FromSeconds(3));
        }
        catch (AggregateException) { /* Shutdown only. */ }
        _shutdown.Dispose();
        _signal.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Job
    {
        public Job(string hash, IImageSource source, ThumbnailLevel level, long order)
        {
            Hash = hash;
            Source = source;
            Level = level;
            Order = order;
        }

        public string Hash { get; }
        public IImageSource Source { get; }
        public ThumbnailLevel Level { get; }
        public long Order { get; set; }
        public bool Running { get; set; }
    }
}
