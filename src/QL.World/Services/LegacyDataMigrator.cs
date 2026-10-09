using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;

namespace QL.World.Services;

/// <summary>
/// M11-E: one-shot migration of pre-M11 rows into the managed library.
/// For every <c>images</c> row whose <c>library_path</c> is still NULL:
/// <list type="bullet">
/// <item>source='local' + bytes present on disk → CAS-copy the original
/// file into <c>library\files\</c>, create the category hardlink, then
/// register <c>library_files</c> and stamp library_path / entry_code on
/// the row (same per-row transaction).</item>
/// <item>source='remote' → skipped + logged (M11 NAS bypass; remote
/// rows are migrated later when NAS work resumes).</item>
/// <item>source file missing → skipped (is_missing semantics untouched;
/// the row keeps working with the legacy path until the file returns).</item>
/// </list>
/// Idempotent: re-running only touches rows with NULL library_path;
/// already-created hardlinks are treated as success by the storage layer.
/// </summary>
public sealed class LegacyDataMigrator
{
    private readonly DatabaseService _db;
    private readonly string _libraryRoot;
    private readonly LibraryStorageService _storage;
    private readonly Action<string> _log;

    public LegacyDataMigrator(
        DatabaseService db,
        string libraryRoot,
        LibraryStorageService? storage = null,
        Action<string>? log = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));
        _libraryRoot = libraryRoot;
        _storage = storage ?? new LibraryStorageService();
        _log = log ?? (_ => { });
    }

    public sealed class Result
    {
        public int TotalCandidates { get; set; }
        public int Migrated { get; set; }
        public int SkippedMissing { get; set; }
        public int SkippedRemote { get; set; }
        public int Failed { get; set; }
        public List<string> LogLines { get; } = [];
    }

    /// <summary>
    /// Runs the migration. Cancellation is row-granular: already stamped
    /// rows stay migrated; the next launch resumes from the remaining
    /// NULL-library_path rows.
    /// </summary>
    public Result Run(IProgress<(int Processed, int Total, string Current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LibraryRootResolver.EnsureLibraryLayout(_libraryRoot);

        var rows = LoadCandidates();
        var result = new Result { TotalCandidates = rows.Count };
        int processed = 0;

        foreach (Candidate row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress?.Report((processed, rows.Count, row.SourcePath));

            if (string.Equals(row.Source, "remote", StringComparison.OrdinalIgnoreCase))
            {
                result.SkippedRemote++;
                string msg = $"跳过（NAS 远程行，M11 不迁移）：id={row.Id}";
                result.LogLines.Add(msg);
                _log($"[MIGRATE] {msg}");
                continue;
            }

            if (!File.Exists(row.SourcePath))
            {
                result.SkippedMissing++;
                string msg = $"跳过（原始文件不存在）：id={row.Id} {row.SourcePath}";
                result.LogLines.Add(msg);
                _log($"[MIGRATE] {msg}");
                continue;
            }

            try
            {
                MigrateOne(row);
                result.Migrated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or Microsoft.Data.Sqlite.SqliteException)
            {
                result.Failed++;
                string msg = $"迁移失败（{ex.GetType().Name}: {ex.Message}）：id={row.Id} {row.SourcePath}";
                result.LogLines.Add(msg);
                _log($"[MIGRATE] {msg}");
            }
        }

        _log($"[MIGRATE] Done. migrated={result.Migrated} missing={result.SkippedMissing} " +
             $"remote={result.SkippedRemote} failed={result.Failed}");
        return result;
    }

    private void MigrateOne(Candidate row)
    {
        string fileName = Path.GetFileName(row.SourcePath);
        string category = LibraryRootResolver.SanitizeCategoryName(row.PrimaryCategory);

        // Reserve the code in its own transaction first so file names are
        // stable before any byte copy (mirrors ImportService M11-D).
        string entryCode = _db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, DateOnly.FromDateTime(DateTime.Now), row.Hash));

        string physicalPath = _storage.EnsurePhysicalFromFile(
            _libraryRoot, row.Hash, row.SourcePath, entryCode);

        string hardlinkPath = LibraryRootResolver.GetCategoryHardlinkPath(
            _libraryRoot, category, fileName, entryCode);
        _storage.CreateCategoryHardlink(physicalPath, hardlinkPath);

        _db.RunInTransaction((conn, tx) =>
        {
            using (var lib = conn.CreateCommand())
            {
                lib.Transaction = tx;
                lib.CommandText = """
                    INSERT OR IGNORE INTO library_files
                        (content_hash, physical_path, file_size, width, height,
                         format, entry_code, created_at)
                    SELECT $hash, $phys, file_size, width, height, format, $code, $now
                    FROM images WHERE id = $id;
                    """;
                lib.Parameters.AddWithValue("$hash", row.Hash);
                lib.Parameters.AddWithValue("$phys", physicalPath);
                lib.Parameters.AddWithValue("$code", entryCode);
                lib.Parameters.AddWithValue("$now", DbTime.Now());
                lib.Parameters.AddWithValue("$id", row.Id);
                lib.ExecuteNonQuery();
            }
            using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE images
                    SET library_path = $link, entry_code = $code, updated_at = $now
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$link", hardlinkPath);
                update.Parameters.AddWithValue("$code", entryCode);
                update.Parameters.AddWithValue("$now", DbTime.Now());
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
            }
            return true;
        });
    }

    private List<Candidate> LoadCandidates()
    {
        return _db.Run(conn =>
        {
            var rows = new List<Candidate>();
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT id, file_hash, COALESCE(original_path, file_path),
                       primary_category, COALESCE(source, 'local')
                FROM images
                WHERE library_path IS NULL
                ORDER BY id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Candidate(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4)));
            }
            return rows;
        });
    }

    private sealed record Candidate(
        long Id, string Hash, string SourcePath, string? PrimaryCategory, string Source);
}
