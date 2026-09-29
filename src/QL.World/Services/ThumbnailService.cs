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
/// Output layout: thumbnails\&lt;hash2&gt;\&lt;hash&gt;.webp.
/// This service has no UI dependency — consumers get file paths via
/// <see cref="ThumbnailReady"/> and load bitmaps themselves.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int WORKER_COUNT = 2;
    private const int PLACEHOLDER_SIZE = 64;

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

        _workers = Enumerable.Range(0, WORKER_COUNT)
            .Select(_ => Task.Run(ConsumeLoop))
            .ToArray();

        _placeholderTask = Task.Run(EnsurePlaceholderOnDisk);
    }

    /// <summary>Raised (on a worker thread) when a thumbnail becomes available.</summary>
    public event Action<string>? ThumbnailReady;

    public int PendingCount
    {
        get { lock (_queueGate) return _queue.Count; }
    }

    public string GetThumbnailPath(string hash)
    {
        string key = NormalizeHash(hash);
        return Path.Combine(_root, key[..2], $"{key}.webp");
    }

    public bool ThumbnailExists(string hash) => File.Exists(GetThumbnailPath(hash));

    /// <summary>Enqueues generation unless the WebP already exists.</summary>
    /// <param name="source">M5-E: replaces the <c>string sourcePath</c> overload
    /// so the same worker handles local files (<see cref="LocalImageSource"/>)
    /// and remote NAS bytes (<see cref="RemoteImageSource"/>).</param>
    public void Request(string hash, IImageSource source, bool highPriority = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        string key = NormalizeHash(hash);
        if (ThumbnailExists(key))
            return;

        lock (_queueGate)
        {
            if (_jobs.ContainsKey(key))
                return;

            long order = highPriority ? --_priorityOrder : --_normalOrder;
            var job = new Job(key, source, order);
            _jobs[key] = job;
            _queue.Enqueue(key, order);
        }
        _signal.Set();
    }

    /// <summary>Promotes a queued job ahead of normal (previously enqueued) jobs.</summary>
    public void Prioritize(string hash)
    {
        string key = NormalizeHash(hash);
        lock (_queueGate)
        {
            if (!_jobs.TryGetValue(key, out Job? job) || job.Running)
                return;

            job.Order = --_priorityOrder;
            // Re-enqueue with the new priority; stale dequeues are guarded
            // by comparing the stored order.
            _queue.Enqueue(key, job.Order);
        }
        _signal.Set();
    }

    /// <summary>Forces regeneration even when a WebP already exists.</summary>
    public void Regenerate(string hash, IImageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string key = NormalizeHash(hash);
        try { File.Delete(GetThumbnailPath(key)); }
        catch (IOException) { /* Will be overwritten on save. */ }

        lock (_queueGate)
        {
            _jobs.Remove(key);
        }
        Request(key, source, highPriority: true);
    }

    public Task WaitPlaceholderAsync() => _placeholderTask ?? Task.CompletedTask;

    private void ConsumeLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            string? key = null;
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
                        key = candidate;
                        job = queued;
                        job.Running = true;
                        break;
                    }
                }
            }

            if (key is null || job is null)
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
                    $"[Thumbnail] {job.Hash} failed: {ex.Message}");
                CopyPlaceholderFor(job.Hash);
            }

            lock (_queueGate)
            {
                _jobs.Remove(key);
            }
            ThumbnailReady?.Invoke(key);
        }
    }

    private void ProcessJob(Job job)
    {
        string output = GetThumbnailPath(job.Hash);
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
                CopyPlaceholderFor(job.Hash);
                return;
            }

            int size = _settings.ThumbnailSize;
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

    private void CopyPlaceholderFor(string hash)
    {
        try
        {
            string placeholder = EnsurePlaceholderOnDisk();
            string output = GetThumbnailPath(hash);
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
        public Job(string hash, IImageSource source, long order)
        {
            Hash = hash;
            Source = source;
            Order = order;
        }

        public string Hash { get; }
        public IImageSource Source { get; }
        public long Order { get; set; }
        public bool Running { get; set; }
    }
}
