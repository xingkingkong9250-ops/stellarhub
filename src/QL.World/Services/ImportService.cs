using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Nas;
using QL.World.Services.Pbr;

namespace QL.World.Services;

/// <summary>
/// Background import pipeline: scan → hash dedupe → metadata probe →
/// 50-row batched transactions. Cancellation is file-granular: already
/// committed batches stay (re-running is idempotent).
///
/// M5-D: file-system access goes through <see cref="IFileSystemAccessor"/>.
/// Local imports resolve <see cref="LocalFileSystemAccessor"/> (behavior
/// unchanged from pre-M5); remote imports are handed a
/// <see cref="RemoteFileSystemAccessor"/> by the caller (e.g.
/// <see cref="RemoteLibraryScanner"/>). The per-file loop is branch-free:
/// hash + metadata both stream through <see cref="IFileSystemAccessor.OpenRead"/>
/// so the same code handles local FileStreams and remote network streams.
/// </summary>
public sealed class ImportService
{
    private const int BATCH_SIZE = 50;

    private readonly DatabaseService _db;
    private readonly ImageInspector _inspector;
    private readonly PbrGroupingService? _pbrGrouping;

    public ImportService(DatabaseService db, ImageInspector inspector, PbrGroupingService? pbrGrouping = null)
    {
        _db = db;
        _inspector = inspector;
        _pbrGrouping = pbrGrouping;
    }

    /// <summary>
    /// Local import: resolves <see cref="LocalFileSystemAccessor.Instance"/>.
    /// Existing call sites (ImportViewModel, FileWatcherService) are unchanged.
    /// </summary>
    public Task<ImportReport> ImportAsync(
        ImportOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        // The token is observed per file inside ImportCore (file-granular
        // cancellation), so it must not be passed to Task.Run (which would
        // skip the delegate entirely when pre-cancelled).
        Task.Run(() => ImportCore(options, LocalFileSystemAccessor.Instance, progress, cancellationToken));

    /// <summary>
    /// Import with a caller-provided <see cref="IFileSystemAccessor"/>. M5-D
    /// remote path: <see cref="RemoteLibraryScanner"/> builds a
    /// <see cref="RemoteFileSystemAccessor"/> and passes it here with
    /// <see cref="ImportOptions.Source"/> = <see cref="ImageSource.Remote"/>.
    /// Tests inject a stub accessor. <paramref name="accessor"/> is required
    /// for remote options and ignored (replaced by Local) for local options.
    /// </summary>
    public Task<ImportReport> ImportAsync(
        ImportOptions options,
        IFileSystemAccessor accessor,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        // Source==Remote ⇒ the caller (RemoteLibraryScanner / tests) is
        // contractually responsible for handing a remote-capable accessor;
        // no runtime type check so test stubs aren't rejected.
        return Task.Run(() => ImportCore(options, accessor, progress, cancellationToken));
    }

    private ImportReport ImportCore(
        ImportOptions options,
        IFileSystemAccessor accessor,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var report = new ImportReport();
        ExistingSnapshot snapshot = LoadExisting();
        List<string> files = ScanFiles(options, accessor, report.LogLines);
        report.ScannedTotal = files.Count;

        var state = new ImportProgress { ScannedTotal = files.Count };
        var pending = new List<PendingImage>(BATCH_SIZE);
        var affectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (pending.Count == 0) return;
            List<PendingImage> batch = pending;
            pending = new List<PendingImage>(BATCH_SIZE);

            List<ImportedItem> inserted = InsertBatch(batch, options);
            report.Added.AddRange(inserted);
            foreach (ImportedItem item in inserted)
            {
                // Local rows track the parent dir for PBR rescanning; remote
                // rows have no local dir (WebDAV URL), so PBR grouping is
                // skipped for them — matches pre-M5 behavior where remote
                // textures are not part of local PBR auto-groups.
                if (options.Source == ImageSource.Local)
                {
                    string? dir = Path.GetDirectoryName(item.FilePath);
                    if (dir is not null)
                        affectedDirs.Add(dir);
                }
            }
            state.Added = report.Added.Count;
            state.Skipped = report.SkippedCount;
            progress?.Report(CloneProgress(state));
        }

        // PBR auto-grouping for the touched directories; never blocks the import result.
        void RescanPbrGroups()
        {
            if (affectedDirs.Count == 0 || _pbrGrouping is null)
                return;
            try
            {
                _pbrGrouping.RescanDirectories(affectedDirs);
            }
            catch (Exception ex)
            {
                report.LogLines.Add($"贴图组扫描失败（不影响导入结果）：{ex.Message}");
            }
        }

        foreach (string file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Flush();
                RescanPbrGroups();
                report.Cancelled = true;
                return report;
            }

            state.Processed++;
            state.CurrentFile = file;
            progress?.Report(CloneProgress(state));

            try
            {
                // Canonical path: local paths are Path.GetFullPath-normalized
                // by LocalFileSystemAccessor.EnumerateFiles; remote paths are
                // nas://{id}{remotePath}. Either way the value returned by
                // LoadExisting matches byte-for-byte, so no re-normalization.
                if (snapshot.Paths.Contains(file))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（库中已有路径）：{file}");
                    continue;
                }

                long size = accessor.GetFileSize(file);
                if (size < 0)
                {
                    report.SkippedCount++;
                    continue;
                }

                string hash;
                using (Stream hashStream = accessor.OpenRead(file))
                {
                    hash = Md5Hasher.ComputeStream(hashStream);
                }
                if (snapshot.Hashes.Contains(hash))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（内容重复）：{file}");
                    continue;
                }

                int width;
                int height;
                string? format;
                bool decoded;
                using (Stream metaStream = accessor.OpenRead(file))
                {
                    decoded = _inspector.TryReadMetadata(metaStream, out width, out height, out format);
                }
                if (!decoded)
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（无法解码/已损坏）：{file}");
                    continue;
                }

                pending.Add(new PendingImage(
                    file,
                    GetFileName(file, options.Source),
                    hash,
                    size,
                    width,
                    height,
                    format,
                    options.Source,
                    options.NasConnectionId));
                snapshot.Hashes.Add(hash);
                snapshot.Paths.Add(file);

                if (pending.Count >= BATCH_SIZE)
                    Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NasConnectionException)
            {
                report.SkippedCount++;
                report.LogLines.Add($"跳过（读取失败 {ex.GetType().Name}）：{file}");
            }
        }

        Flush();
        RescanPbrGroups();
        state.CurrentFile = string.Empty;
        state.Added = report.Added.Count;
        state.Skipped = report.SkippedCount;
        progress?.Report(CloneProgress(state));
        return report;
    }

    private static ImportProgress CloneProgress(ImportProgress state) => new()
    {
        ScannedTotal = state.ScannedTotal,
        Processed = state.Processed,
        Added = state.Added,
        Skipped = state.Skipped,
        CurrentFile = state.CurrentFile,
    };

    private List<ImportedItem> InsertBatch(List<PendingImage> batch, ImportOptions options)
    {
        return _db.RunInTransaction((conn, tx) =>
        {
            var inserted = new List<ImportedItem>();
            string now = DbTime.Now();
            string source = options.Source == ImageSource.Remote ? "remote" : "local";

            foreach (PendingImage item in batch)
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO images
                        (file_path, file_name, sortable_name, file_hash, file_size,
                         width, height, format, rating, notes, primary_category,
                         dominant_colors, is_missing, created_at, updated_at,
                         source, nas_connection_id)
                    VALUES
                        ($path, $name, $sortable, $hash, $size,
                         $width, $height, $format, 0, '', NULL,
                         NULL, 0, $now, $now,
                         $source, $nasConn);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$path", item.FilePath);
                insert.Parameters.AddWithValue("$name", item.FileName);
                insert.Parameters.AddWithValue("$sortable", item.FileName.ToLowerInvariant());
                insert.Parameters.AddWithValue("$hash", item.Hash);
                insert.Parameters.AddWithValue("$size", item.FileSize);
                insert.Parameters.AddNullable("$width", item.Width);
                insert.Parameters.AddNullable("$height", item.Height);
                insert.Parameters.AddNullable("$format", item.Format);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$source", source);
                insert.Parameters.AddNullable("$nasConn", item.NasConnectionId);

                long id = Convert.ToInt64(insert.ExecuteScalar());
                inserted.Add(new ImportedItem(id, item.FilePath, item.Hash));
            }
            return inserted;
        });
    }

    private static List<string> ScanFiles(
        ImportOptions options,
        IFileSystemAccessor accessor,
        List<string> log)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = options.EnabledExtensions.Count > 0
            ? options.EnabledExtensions
            : ImportOptions.SupportedExtensions;

        foreach (string input in options.InputPaths)
        {
            try
            {
                // Probe the root so a missing path is logged (not silently
                // empty). GetFileInfo returns null for non-existent roots on
                // both drivers; for a file root it yields the file itself,
                // for a directory root it triggers enumeration.
                NasFileInfo? rootInfo = accessor.GetFileInfo(input);
                if (rootInfo is null)
                {
                    log.Add($"路径不存在：{input}");
                    continue;
                }

                if (!rootInfo.IsDirectory)
                {
                    if (ExtensionMatches(input, extensions))
                        files.Add(input);
                    continue;
                }

                foreach (string file in accessor.EnumerateFiles(
                             input, extensions, options.RecurseSubfolders, options.SkipHidden))
                    files.Add(file);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                or NasConnectionException)
            {
                log.Add($"无法扫描路径：{input}（{ex.Message}）");
            }
        }

        return [.. files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool ExtensionMatches(string path, IReadOnlySet<string> extensions)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0) return false;
        return extensions.Contains(path[dot..]);
    }

    /// <summary>
    /// File-name segment for sortable_name. Local paths use the OS separator;
    /// remote canonical paths use forward slashes under nas://{id}{path}.
    /// </summary>
    private static string GetFileName(string filePath, ImageSource source)
    {
        char sep = source == ImageSource.Remote ? '/' : Path.DirectorySeparatorChar;
        int idx = filePath.LastIndexOf(sep);
        return idx < 0 ? filePath : filePath[(idx + 1)..];
    }

    private ExistingSnapshot LoadExisting()
    {
        return _db.Run(conn =>
        {
            var snapshot = new ExistingSnapshot();
            using (var command = conn.CreateCommand())
            {
                // M5-D: only normalize local rows — Path.GetFullPath on a
                // nas:// canonical would throw / mangle. source is null for
                // pre-v6 rows (treated as local) or 'local'/'remote'.
                command.CommandText = "SELECT file_path, source FROM images;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string raw = reader.GetString(0);
                    bool isRemote = !reader.IsDBNull(1) && reader.GetString(1) == "remote";
                    snapshot.Paths.Add(isRemote ? raw : Path.GetFullPath(raw));
                }
            }
            using (var command = conn.CreateCommand())
            {
                command.CommandText = "SELECT file_hash FROM images;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    snapshot.Hashes.Add(reader.GetString(0));
            }
            return snapshot;
        });
    }

    private sealed record PendingImage(
        string FilePath,
        string FileName,
        string Hash,
        long FileSize,
        int Width,
        int Height,
        string? Format,
        ImageSource Source,
        int? NasConnectionId);

    private sealed class ExistingSnapshot
    {
        public HashSet<string> Hashes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
