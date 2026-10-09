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
///
/// M11: when a managed library root is configured (W7 wizard), each new
/// row is content-addressed into <c>library\files\</c> (CAS, one physical
/// file per hash) and exposed through an NTFS hardlink under
/// <c>library\&lt;一级分类&gt;\</c> named 原名_入库编码. Dedupe is then
/// (hash, category)-scoped: the same bytes may be linked into multiple
/// categories but never twice into one. Without a configured root the
/// importer runs in legacy mode (rows point straight at the source path,
/// library columns mirror file_hash/file_path) — used by tests and the
/// defensive pre-wizard path.
/// </summary>
public sealed class ImportService
{
    private const int BATCH_SIZE = 50;

    private readonly DatabaseService _db;
    private readonly ImageInspector _inspector;
    private readonly PbrGroupingService? _pbrGrouping;
    private readonly string? _libraryRoot;
    private readonly LibraryStorageService _storage;

    public ImportService(
        DatabaseService db,
        ImageInspector inspector,
        PbrGroupingService? pbrGrouping = null,
        string? libraryRootPath = null,
        LibraryStorageService? storage = null)
    {
        _db = db;
        _inspector = inspector;
        _pbrGrouping = pbrGrouping;
        _libraryRoot = string.IsNullOrWhiteSpace(libraryRootPath) ? null : libraryRootPath;
        _storage = storage ?? new LibraryStorageService();
    }

    /// <summary>M11: true when bytes are copied into the managed library.</summary>
    private bool ManagedStorage => _libraryRoot is not null;

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
                // M11: use the ORIGINAL source dir — the displayed FilePath is
                // now the category hardlink under the library, where texture
                // sets from different source folders would be mixed.
                if (options.Source == ImageSource.Local)
                {
                    string? dir = Path.GetDirectoryName(item.OriginalPath);
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
                if (snapshot.OriginalPaths.Contains(file))
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

                // M11: dedupe is (hash, category)-scoped. The same bytes may
                // be linked into several categories but never twice into one.
                // Legacy mode has no category dimension and behaves like the
                // old global hash dedupe (everything lands in 未分类).
                string category = LibraryRootResolver.SanitizeCategoryName(options.PrimaryCategory);
                if (snapshot.HashCategories.Contains((hash, category)))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（同分类内容重复）：{file} → {category}");
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

                string originalName = GetFileName(file, options.Source);
                string rowPath = file;
                string? entryCode = null;
                string? physicalPath = null;

                if (ManagedStorage)
                {
                    // Reserve the human-readable entry code in its own
                    // transaction so the counter increment survives even if
                    // the 50-row batch later rolls back (orphan physical file
                    // risk is bounded: a later CAS hit on the same hash simply
                    // reuses the shard path).
                    entryCode = _db.RunInTransaction((conn, tx) =>
                        EntryCodeGenerator.Next(
                            conn, tx, DateOnly.FromDateTime(DateTime.Now), hash));

                    snapshot.TryGetPhysicalPath(hash, out string? existingPhysical);
                    using (Stream copyStream = accessor.OpenRead(file))
                    {
                        physicalPath = _storage.EnsurePhysical(
                            _libraryRoot!, hash, copyStream, originalName, entryCode,
                            existingPhysical);
                    }
                    snapshot.RecordPhysicalPath(hash, physicalPath);

                    string categoryLink = LibraryRootResolver.GetCategoryHardlinkPath(
                        _libraryRoot!, category, originalName, entryCode);
                    _storage.CreateCategoryHardlink(physicalPath, categoryLink);
                    rowPath = categoryLink;
                }

                pending.Add(new PendingImage(
                    rowPath,
                    file,
                    originalName,
                    hash,
                    size,
                    width,
                    height,
                    format,
                    options.Source,
                    options.NasConnectionId,
                    category,
                    physicalPath,
                    entryCode));
                snapshot.HashCategories.Add((hash, category));
                snapshot.OriginalPaths.Add(file);

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
                // M11: register the CAS physical file first (one row per
                // hash; cross-category rows in the same batch hit the
                // IGNORE). library_files.entry_code is the code of the row
                // that first materialized the bytes; per-row codes live in
                // images.entry_code and the category hardlink file names.
                if (item.PhysicalPath is not null && item.EntryCode is not null)
                {
                    using var lib = conn.CreateCommand();
                    lib.Transaction = tx;
                    lib.CommandText = """
                        INSERT OR IGNORE INTO library_files
                            (content_hash, physical_path, file_size, width, height,
                             format, entry_code, created_at)
                        VALUES
                            ($hash, $phys, $size, $width, $height,
                             $format, $code, $now);
                        """;
                    lib.Parameters.AddWithValue("$hash", item.Hash);
                    lib.Parameters.AddWithValue("$phys", item.PhysicalPath);
                    lib.Parameters.AddWithValue("$size", item.FileSize);
                    lib.Parameters.AddNullable("$width", item.Width);
                    lib.Parameters.AddNullable("$height", item.Height);
                    lib.Parameters.AddNullable("$format", item.Format);
                    lib.Parameters.AddWithValue("$code", item.EntryCode);
                    lib.Parameters.AddWithValue("$now", now);
                    lib.ExecuteNonQuery();
                }

                // M11: primary_category carries the resolved one-level
                // category (NULL for 未分类 to preserve the historic
                // "no category" DB semantics). The 4 library columns are
                // always written: legacy mode mirrors file_hash/file_path
                // into library_hash/original_path exactly like the v9
                // backfill, with library_path/entry_code left NULL.
                string? primaryCategory =
                    item.Category == LibraryRootResolver.UncategorizedCategoryName
                        ? null
                        : item.Category;

                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO images
                        (file_path, file_name, sortable_name, file_hash, file_size,
                         width, height, format, rating, notes, primary_category,
                         dominant_colors, is_missing, created_at, updated_at,
                         source, nas_connection_id,
                         library_hash, library_path, original_path, entry_code)
                    VALUES
                        ($path, $name, $sortable, $hash, $size,
                         $width, $height, $format, 0, '', $category,
                         NULL, 0, $now, $now,
                         $source, $nasConn,
                         $hash, $libPath, $original, $entryCode);
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
                insert.Parameters.AddNullable("$category", primaryCategory);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$source", source);
                insert.Parameters.AddNullable("$nasConn", item.NasConnectionId);
                // library_path is the category hardlink = FilePath in managed
                // mode; NULL in legacy mode (PhysicalPath is null there).
                insert.Parameters.AddWithValue("$libPath",
                    item.PhysicalPath is not null ? item.FilePath : (object)DBNull.Value);
                insert.Parameters.AddWithValue("$original", item.OriginalPath);
                insert.Parameters.AddWithValue("$entryCode",
                    (object?)item.EntryCode ?? DBNull.Value);

                long id = Convert.ToInt64(insert.ExecuteScalar());
                inserted.Add(new ImportedItem(id, item.FilePath, item.Hash, item.OriginalPath));
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
                // M11: identity of "already imported this source" is the
                // original external path (pre-M11 rows have original_path
                // backfilled from file_path by the v9 migration). Local rows
                // are Path.GetFullPath-normalized; nas:// canonicals must not
                // touch Path.GetFullPath.
                command.CommandText =
                    "SELECT COALESCE(original_path, file_path), source FROM images;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string raw = reader.GetString(0);
                    bool isRemote = !reader.IsDBNull(1) && reader.GetString(1) == "remote";
                    snapshot.OriginalPaths.Add(isRemote ? raw : Path.GetFullPath(raw));
                }
            }
            using (var command = conn.CreateCommand())
            {
                // M11: (hash, category) pairs drive dedupe. NULL categories
                // map to 未分类 exactly like the import-time resolution.
                command.CommandText = "SELECT file_hash, primary_category FROM images;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string hash = reader.GetString(0);
                    string? category = reader.IsDBNull(1) ? null : reader.GetString(1);
                    snapshot.HashCategories.Add(
                        (hash, LibraryRootResolver.SanitizeCategoryName(category)));
                }
            }
            using (var command = conn.CreateCommand())
            {
                // M11: known physical shard paths — cross-category re-imports
                // hardlink to the existing CAS file instead of recopying.
                command.CommandText =
                    "SELECT content_hash, physical_path FROM library_files;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    snapshot.RecordPhysicalPath(reader.GetString(0), reader.GetString(1));
            }
            return snapshot;
        });
    }

    private sealed record PendingImage(
        string FilePath,
        string OriginalPath,
        string FileName,
        string Hash,
        long FileSize,
        int Width,
        int Height,
        string? Format,
        ImageSource Source,
        int? NasConnectionId,
        string Category,
        string? PhysicalPath,
        string? EntryCode);

    private sealed class ExistingSnapshot
    {
        /// <summary>(content hash, resolved category) pairs already in the library.</summary>
        public HashSet<(string Hash, string Category)> HashCategories { get; } =
            new(HashCategoryComparer.Instance);

        /// <summary>External/source paths already imported (COALESCE original_path).</summary>
        public HashSet<string> OriginalPaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _physicalByHash =
            new(StringComparer.Ordinal);

        public bool TryGetPhysicalPath(string hash, out string? physicalPath)
            => _physicalByHash.TryGetValue(hash, out physicalPath);

        public void RecordPhysicalPath(string hash, string physicalPath)
            => _physicalByHash[hash] = physicalPath;

        private sealed class HashCategoryComparer : IEqualityComparer<(string Hash, string Category)>
        {
            public static readonly HashCategoryComparer Instance = new();
            public bool Equals((string Hash, string Category) x, (string Hash, string Category) y)
                => string.Equals(x.Hash, y.Hash, StringComparison.Ordinal)
                   && string.Equals(x.Category, y.Category, StringComparison.Ordinal);
            public int GetHashCode((string Hash, string Category) obj)
                => HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(obj.Hash),
                    StringComparer.Ordinal.GetHashCode(obj.Category));
        }
    }
}
