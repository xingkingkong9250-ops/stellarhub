using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;

namespace QL.World.Services;

/// <summary>PEND-003: live progress for a library-root relocation.</summary>
public sealed record LibraryRelocationProgress(
    int FilesCopied,
    int FilesTotal,
    long BytesCopied,
    long BytesTotal,
    string CurrentItem)
{
    /// <summary>0–100; byte-weighted when sizes are known, file-weighted otherwise.</summary>
    public double Percent
    {
        get
        {
            if (BytesTotal > 0)
                return Math.Clamp(BytesCopied * 100d / BytesTotal, 0, 100);
            if (FilesTotal > 0)
                return Math.Clamp(FilesCopied * 100d / FilesTotal, 0, 100);
            return 100;
        }
    }
}

/// <summary>PEND-003: outcome of a successful relocation.</summary>
public sealed record LibraryRelocationResult(
    string NewRoot,
    int FilesCopied,
    long BytesCopied,
    TimeSpan Duration);

/// <summary>
/// PEND-003: raised when a relocation failed after the copy started. The
/// target directory has already been rolled back when this is thrown.
/// </summary>
public sealed class LibraryRelocationException : Exception
{
    public LibraryRelocationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// PEND-003: moves the software-managed library to a new folder.
///
/// <para>The whole data root is copied (db + thumbnails + models + CAS
/// files + category hardlinks), the copied database is rewritten so every
/// absolute path points at the new root, and the copied database is
/// validated with <c>PRAGMA quick_check</c>. Only then does
/// <see cref="AppSettingsService.ConfigureLibraryRoot"/> switch the
/// configured root. The new root takes effect after the user restarts the
/// app; the old folder is kept as a backup.</para>
///
/// <para>Consistency: the copy runs while <see cref="DatabaseService"/>
/// holds its maintenance lock and after a WAL TRUNCATE checkpoint, so the
/// copied <c>qlworld.db</c> is a standalone, consistent snapshot. The
/// <c>-wal</c>/<c>-shm</c> sidecars and <c>.tmp-*</c> leftovers are never
/// copied.</para>
/// </summary>
public sealed class LibraryRelocationService
{
    private const long ProgressIntervalMs = 100;

    private readonly AppSettingsService _settings;
    private readonly DatabaseService _database;
    private readonly Action<string> _log;

    public LibraryRelocationService(
        AppSettingsService settings,
        DatabaseService database,
        Action<string>? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> with a user-facing
    /// message when the chosen folder cannot be a relocation target.
    /// Never creates directories.
    /// </summary>
    public void ValidateTarget(string newRoot)
    {
        if (string.IsNullOrWhiteSpace(newRoot))
            throw new InvalidOperationException("请选择新的素材库目录。");

        if (string.IsNullOrWhiteSpace(_settings.LibraryRootPath))
            throw new InvalidOperationException("当前尚未配置素材库目录，无法迁移。");

        string source = NormalizeRoot(_settings.LibraryRootPath);
        string target;
        try
        {
            target = NormalizeRoot(newRoot);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"目标路径无效：{ex.Message}", ex);
        }

        if (!Directory.Exists(source))
            throw new InvalidOperationException($"当前素材库目录不存在：{source}");

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标目录与当前素材库目录相同。");

        if (IsWithin(target, source) || IsWithin(source, target))
            throw new InvalidOperationException(
                "目标目录不能位于当前素材库目录内，也不能包含当前素材库目录。");

        if (Directory.Exists(target) &&
            Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidOperationException(
                "目标目录必须为空目录（或尚不存在）。请新建一个空文件夹后再选择。");
        }
    }

    /// <summary>
    /// Copies the library, rewrites paths, validates the copy and switches
    /// the configured root. On any failure the newly created target entries
    /// are deleted and the configured root is left untouched.
    /// </summary>
    public Task<LibraryRelocationResult> RelocateAsync(
        string newRoot,
        IProgress<LibraryRelocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ValidateTarget(newRoot);
        string source = NormalizeRoot(_settings.LibraryRootPath!);
        string target = NormalizeRoot(newRoot);
        // Monitor ownership is thread-affine, so the whole copy runs on one
        // thread-pool thread (no awaits inside RelocateCore).
        return Task.Run(() => RelocateCore(source, target, progress, ct), ct);
    }

    private LibraryRelocationResult RelocateCore(
        string source,
        string target,
        IProgress<LibraryRelocationProgress>? progress,
        CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool targetExisted = Directory.Exists(target);
        var createdTopLevel = new List<string>();
        int copiedFiles = 0;
        long copiedBytes = 0;

        try
        {
            using (_database.AcquireMaintenanceLock())
            {
                _database.CheckpointWal();
                Directory.CreateDirectory(target);

                var plan = BuildCopyPlan(source);
                long bytesTotal = plan.Sum(entry => entry.Length);
                int filesTotal = plan.Count;
                var reportClock = Stopwatch.StartNew();

                progress?.Report(new LibraryRelocationProgress(
                    0, filesTotal, 0, bytesTotal, string.Empty));

                foreach (var entry in plan)
                {
                    ct.ThrowIfCancellationRequested();

                    string destination = Path.Combine(target, entry.RelativePath);
                    string? destinationDirectory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                        Directory.CreateDirectory(destinationDirectory);

                    string topLevel = GetTopLevelSegment(entry.RelativePath);
                    if (!createdTopLevel.Contains(topLevel))
                        createdTopLevel.Add(topLevel);

                    File.Copy(
                        Path.Combine(source, entry.RelativePath),
                        destination,
                        overwrite: false);

                    copiedFiles++;
                    copiedBytes += entry.Length;

                    if (progress is not null &&
                        (reportClock.ElapsedMilliseconds >= ProgressIntervalMs ||
                         copiedFiles == filesTotal))
                    {
                        reportClock.Restart();
                        progress.Report(new LibraryRelocationProgress(
                            copiedFiles, filesTotal, copiedBytes, bytesTotal,
                            entry.RelativePath));
                    }
                }

                progress?.Report(new LibraryRelocationProgress(
                    copiedFiles, filesTotal, copiedBytes, bytesTotal, string.Empty));

                VerifyCopiedCounts(target, filesTotal, bytesTotal);
                string newDatabasePath = Path.Combine(
                    target, AppDataRootResolver.DatabaseFileName);
                RewriteDatabasePaths(newDatabasePath, source, target);
                VerifyDatabase(newDatabasePath);

                _settings.ConfigureLibraryRoot(target);
            }
        }
        catch (Exception ex)
        {
            RollbackTarget(target, createdTopLevel, targetExisted);
            _log($"[RELOCATE] Failed {source} → {target}: {ex}");
            if (ex is OperationCanceledException)
                throw;
            throw new LibraryRelocationException(
                $"库目录迁移失败，已回滚：{ex.Message}", ex);
        }

        stopwatch.Stop();
        _log($"[RELOCATE] {source} → {target} done in {stopwatch.Elapsed}.");
        return new LibraryRelocationResult(
            target,
            copiedFiles,
            copiedBytes,
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Lists every file to copy (relative path + length). Root-level SQLite
    /// sidecars and any <c>.tmp-*</c> leftovers are skipped so the copy is a
    /// published snapshot, not in-flight scratch data.
    /// </summary>
    private static List<(string RelativePath, long Length)> BuildCopyPlan(string source)
    {
        var plan = new List<(string, long)>();
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string name = Path.GetFileName(relative);

            if (name.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase))
                continue;

            bool atRoot = !relative.Contains(Path.DirectorySeparatorChar) &&
                          !relative.Contains(Path.AltDirectorySeparatorChar);
            if (atRoot &&
                (name.Equals(
                     AppDataRootResolver.DatabaseFileName + "-wal",
                     StringComparison.OrdinalIgnoreCase) ||
                 name.Equals(
                     AppDataRootResolver.DatabaseFileName + "-shm",
                     StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            plan.Add((relative, new FileInfo(file).Length));
        }

        return plan;
    }

    private static void VerifyCopiedCounts(
        string target, int expectedFiles, long expectedBytes)
    {
        int files = CountFiles(target);
        long bytes = MeasureBytes(target);
        if (files != expectedFiles || bytes != expectedBytes)
        {
            throw new IOException(
                $"复制校验失败：源 {expectedFiles} 个文件 / {expectedBytes} 字节，" +
                $"目标 {files} 个文件 / {bytes} 字节。");
        }
    }

    private static int CountFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();

    private static long MeasureBytes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    /// <summary>
    /// Rewrites every absolute path column from the old root to the new one.
    /// Only values that start with the old root are touched (external
    /// original paths stay as-is).
    /// </summary>
    private static void RewriteDatabasePaths(
        string databasePath, string oldRoot, string newRoot)
    {
        var targets = new (string Table, string Column)[]
        {
            ("images", "file_path"),
            ("images", "library_path"),
            ("images", "original_path"),
            ("library_files", "physical_path"),
            ("download_queue", "local_target"),
        };

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var (table, column) in targets)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE {table}
                       SET {column} = $new || substr({column}, length($old) + 1)
                     WHERE lower(substr({column}, 1, length($old))) = lower($old);
                    """;
                command.Parameters.AddWithValue("$new", newRoot);
                command.Parameters.AddWithValue("$old", oldRoot);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static void VerifyDatabase(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using var reader = command.ExecuteReader();

        var results = new List<string>();
        while (reader.Read())
            results.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);

        if (results.Count != 1 ||
            !string.Equals(results[0], "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "新库数据库完整性校验失败：" + string.Join("；", results));
        }
    }

    private void RollbackTarget(
        string target, List<string> createdTopLevel, bool targetExisted)
    {
        try
        {
            if (!Directory.Exists(target))
                return;

            foreach (string entry in createdTopLevel)
            {
                string path = Path.Combine(target, entry);
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    else if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                }
                catch (Exception ex)
                {
                    _log($"[RELOCATE] Rollback failed for {path}: {ex.Message}");
                }
            }

            // Only remove the folder itself when it did not exist before.
            if (!targetExisted &&
                !Directory.EnumerateFileSystemEntries(target).Any())
            {
                Directory.Delete(target);
            }
        }
        catch (Exception ex)
        {
            _log($"[RELOCATE] Rollback failed for {target}: {ex.Message}");
        }
    }

    private static string NormalizeRoot(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return full.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsWithin(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
            return false;

        string prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTopLevelSegment(string relativePath)
    {
        int index = relativePath.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return index < 0 ? relativePath : relativePath[..index];
    }
}
