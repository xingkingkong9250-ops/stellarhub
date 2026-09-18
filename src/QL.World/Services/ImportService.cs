using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// Background import pipeline: scan → hash dedupe → metadata probe →
/// 50-row batched transactions. Cancellation is file-granular: already
/// committed batches stay (re-running is idempotent).
/// </summary>
public sealed class ImportService
{
    private const int BATCH_SIZE = 50;

    private readonly DatabaseService _db;
    private readonly ImageInspector _inspector;

    public ImportService(DatabaseService db, ImageInspector inspector)
    {
        _db = db;
        _inspector = inspector;
    }

    public Task<ImportReport> ImportAsync(
        ImportOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        // The token is observed per file inside ImportCore (file-granular
        // cancellation), so it must not be passed to Task.Run (which would
        // skip the delegate entirely when pre-cancelled).
        Task.Run(() => ImportCore(options, progress, cancellationToken));

    private ImportReport ImportCore(
        ImportOptions options,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var report = new ImportReport();
        ExistingSnapshot snapshot = LoadExisting();
        List<string> files = ScanFiles(options, report.LogLines);
        report.ScannedTotal = files.Count;

        var state = new ImportProgress { ScannedTotal = files.Count };
        var pending = new List<PendingImage>(BATCH_SIZE);

        void Flush()
        {
            if (pending.Count == 0) return;
            List<PendingImage> batch = pending;
            pending = new List<PendingImage>(BATCH_SIZE);

            List<ImportedItem> inserted = InsertBatch(batch);
            report.Added.AddRange(inserted);
            state.Added = report.Added.Count;
            state.Skipped = report.SkippedCount;
            progress?.Report(CloneProgress(state));
        }

        foreach (string file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Flush();
                report.Cancelled = true;
                return report;
            }

            state.Processed++;
            state.CurrentFile = file;
            progress?.Report(CloneProgress(state));

            try
            {
                string fullPath = Path.GetFullPath(file);

                if (snapshot.Paths.Contains(fullPath))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（库中已有路径）：{fullPath}");
                    continue;
                }

                string hash = Md5Hasher.ComputeFile(fullPath);
                if (snapshot.Hashes.Contains(hash))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（内容重复）：{fullPath}");
                    continue;
                }

                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    report.SkippedCount++;
                    continue;
                }

                if (!_inspector.TryReadMetadata(fullPath, out int width, out int height, out string? format))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（无法解码/已损坏）：{fullPath}");
                    continue;
                }

                pending.Add(new PendingImage(
                    fullPath,
                    Path.GetFileName(fullPath),
                    hash,
                    info.Length,
                    width,
                    height,
                    format));
                snapshot.Hashes.Add(hash);
                snapshot.Paths.Add(fullPath);

                if (pending.Count >= BATCH_SIZE)
                    Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.SkippedCount++;
                report.LogLines.Add($"跳过（读取失败 {ex.GetType().Name}）：{file}");
            }
        }

        Flush();
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

    private List<ImportedItem> InsertBatch(List<PendingImage> batch)
    {
        return _db.RunInTransaction((conn, tx) =>
        {
            var inserted = new List<ImportedItem>();
            string now = DbTime.Now();

            foreach (PendingImage item in batch)
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO images
                        (file_path, file_name, sortable_name, file_hash, file_size,
                         width, height, format, rating, notes, primary_category,
                         dominant_colors, is_missing, created_at, updated_at)
                    VALUES
                        ($path, $name, $sortable, $hash, $size,
                         $width, $height, $format, 0, '', NULL,
                         NULL, 0, $now, $now);
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

                long id = Convert.ToInt64(insert.ExecuteScalar());
                inserted.Add(new ImportedItem(id, item.FilePath, item.Hash));
            }
            return inserted;
        });
    }

    private static List<string> ScanFiles(ImportOptions options, List<string> log)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = options.EnabledExtensions.Count > 0
            ? options.EnabledExtensions
            : ImportOptions.SupportedExtensions;

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = options.RecurseSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = options.SkipHidden
                ? FileAttributes.Hidden | FileAttributes.System
                : FileAttributes.System,
        };

        foreach (string input in options.InputPaths)
        {
            try
            {
                string full = Path.GetFullPath(input);
                if (File.Exists(full))
                {
                    TryAdd(full);
                }
                else if (Directory.Exists(full))
                {
                    foreach (string file in Directory.EnumerateFiles(full, "*", enumeration))
                        TryAdd(file);
                }
                else
                {
                    log.Add($"路径不存在：{input}");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                log.Add($"无法扫描路径：{input}（{ex.Message}）");
            }
        }

        return [.. files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

        void TryAdd(string path)
        {
            if (extensions.Contains(Path.GetExtension(path)))
                files.Add(path);
        }
    }

    private ExistingSnapshot LoadExisting()
    {
        return _db.Run(conn =>
        {
            var snapshot = new ExistingSnapshot();
            using (var command = conn.CreateCommand())
            {
                command.CommandText = "SELECT file_hash FROM images;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    snapshot.Hashes.Add(reader.GetString(0));
            }
            using (var command = conn.CreateCommand())
            {
                command.CommandText = "SELECT file_path FROM images;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    snapshot.Paths.Add(Path.GetFullPath(reader.GetString(0)));
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
        string? Format);

    private sealed class ExistingSnapshot
    {
        public HashSet<string> Hashes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
