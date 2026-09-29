using System.IO;

namespace QL.World.Services;

/// <summary>
/// M5-F: Background LRU cleaner for the thumbnails disk cache.
/// Scans the thumbnails/ directory, sums file sizes, and deletes the
/// least-recently-accessed .webp files until the total drops under
/// <see cref="AppSettingsService.ThumbnailCacheLimitMb"/>. The placeholder
/// file and any .partial / .tmp transient files are never touched.
///
/// Lifecycle: the first sweep runs ~5 s after <see cref="Start"/>, then
/// every 1 h. Each sweep is synchronous but cheap (directory enumeration +
/// FileInfo); large directories may take a few hundred ms, which is
/// acceptable on a background thread.
/// </summary>
public sealed class ThumbnailCacheService : IDisposable
{
    private const int INITIAL_DELAY_MS = 5_000;
    private const int SWEEP_INTERVAL_MS = 60 * 60 * 1000; // 1 hour

    private readonly string _thumbnailsRoot;
    private readonly AppSettingsService _settings;
    private readonly Timer _timer;
    private bool _disposed;

    public ThumbnailCacheService(string dataRoot, AppSettingsService settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _thumbnailsRoot = Path.Combine(dataRoot, AppDataRootResolver.ThumbnailsFolderName);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timer = new Timer(OnTick, state: null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Kicks off the initial sweep + recurring interval.</summary>
    public void Start()
    {
        _timer.Change(INITIAL_DELAY_MS, SWEEP_INTERVAL_MS);
    }

    /// <summary>
    /// Forces an immediate sweep. Used by tests and by callers that just
    /// regenerated many thumbnails and want to reclaim space sooner.
    /// </summary>
    public void TrimNow() => TrimToLimit();

    private void OnTick(object? state) => TrimToLimit();

    /// <summary>
    /// Scans thumbnails/**/*.webp, sums sizes, deletes oldest by
    /// LastAccessTime until under the configured MB limit. Skips:
    /// <list type="bullet">
    ///   <item><c>placeholder.webp</c></item>
    ///   <item><c>*.partial</c> / <c>*.tmp</c> transients</item>
    /// </list>
    /// File-not-found / IOException on individual deletes are swallowed
    /// (best effort — a locked file is skipped this cycle).
    /// </summary>
    private void TrimToLimit()
    {
        long limitBytes = (long)_settings.ThumbnailCacheLimitMb * 1024 * 1024;
        TrimToLimit(limitBytes);
    }

    /// <summary>
    /// Internal overload for tests: forces a trim against an explicit byte
    /// limit (bypasses the AppSettings clamping so a unit test can use a
    /// tiny limit without creating hundreds of MB of files).
    /// </summary>
    internal void TrimNow(long limitBytes) => TrimToLimit(limitBytes);

    private void TrimToLimit(long limitBytes)
    {
        List<FileEntry> files;
        try
        {
            files = EnumerateWebpFiles();
        }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }

        if (files.Count == 0) return;

        long total = 0;
        foreach (FileEntry f in files)
            total += f.Length;

        if (total <= limitBytes)
            return;

        // Sort ascending by LastAccessTime (oldest first = LRU victim).
        files.Sort(static (a, b) => a.LastAccessUtc.CompareTo(b.LastAccessUtc));

        foreach (FileEntry f in files)
        {
            if (total <= limitBytes)
                break;
            try
            {
                File.Delete(f.Path);
                total -= f.Length;
            }
            catch (IOException) { /* Locked or already gone — skip. */ }
            catch (UnauthorizedAccessException) { /* OS lock — skip. */ }
        }
    }

    private List<FileEntry> EnumerateWebpFiles()
    {
        var result = new List<FileEntry>();
        foreach (string dir in Directory.EnumerateDirectories(_thumbnailsRoot))
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.webp"))
            {
                string name = Path.GetFileName(file);
                // Skip placeholder and transients.
                if (name == "placeholder.webp")
                    continue;

                var info = new FileInfo(file);
                if (!info.Exists)
                    continue;

                result.Add(new FileEntry(
                    Path: file,
                    Length: info.Length,
                    // LastAccessTimeUtc can be unreliable on Windows (NTFS
                    // may delay updates). Fall back to LastWriteTimeUtc.
                    LastAccessUtc: info.LastAccessTimeUtc > info.LastWriteTimeUtc
                        ? info.LastAccessTimeUtc
                        : info.LastWriteTimeUtc));
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record FileEntry(string Path, long Length, DateTime LastAccessUtc);
}
