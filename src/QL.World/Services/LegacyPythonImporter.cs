using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using TinyPinyin;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// Imports the Python prototype's SQLite library (M1 image data, tags,
/// collections) into the current dotnet library. Column differences are
/// bridged in-memory: sortable_name = lower(file_name), updated_at = created_at,
/// is_missing = 0, empty category → NULL. Dedupes by path and hash like the
/// normal import pipeline. Embeddings and dominant colors are not migrated
/// (M3 regenerates them).
/// </summary>
public sealed class LegacyPythonImporter
{
    private const int BATCH_SIZE = 50;

    private readonly DatabaseService _db;
    private readonly string? _libraryRoot;
    private readonly LibraryStorageService _storage;

    /// <param name="libraryRoot">
    /// M11: 传入 W7 选定的托管库根时，每条旧版记录按与正常导入一致的
    /// 语义入库（CAS 副本 + 分类硬链接 + 编码）；null 时保持旧版直存。
    /// </param>
    public LegacyPythonImporter(
        DatabaseService db,
        string? libraryRoot = null,
        LibraryStorageService? storage = null)
    {
        _db = db;
        _libraryRoot = string.IsNullOrWhiteSpace(libraryRoot) ? null : libraryRoot;
        _storage = storage ?? new LibraryStorageService();
    }

    /// <summary>Default location of the Python prototype's library file.</summary>
    public static string DefaultLegacyDbPath { get; } =
        @"c:\AI_Programming\APP\QL_World\data\ql_world.db";

    public Task<LegacyImportReport> ImportAsync(
        string legacyDbPath,
        IProgress<LegacyImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportCore(legacyDbPath, progress, cancellationToken));

    private LegacyImportReport ImportCore(
        string legacyDbPath,
        IProgress<LegacyImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDbPath);

        var report = new LegacyImportReport();

        if (!File.Exists(legacyDbPath))
        {
            report.ErrorMessage = $"旧版库文件不存在：{legacyDbPath}";
            return report;
        }

        try
        {
            using var legacy = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = legacyDbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
            legacy.Open();

            if (!TablesExist(legacy))
            {
                report.ErrorMessage = "该文件不是有效的千星汇图 Python 旧版库（缺少 images 表）。";
                return report;
            }

            ExistingSnapshot snapshot = LoadExisting();
            List<LegacyRow> rows = LoadLegacyImages(legacy, report);
            report.ScannedTotal = rows.Count;

            var state = new LegacyImportProgress { ScannedTotal = rows.Count };
            var pending = new List<LegacyRow>(BATCH_SIZE);
            var idMap = new Dictionary<long, long>();

            void Flush()
            {
                if (pending.Count == 0) return;
                List<LegacyRow> batch = pending;
                pending = new List<LegacyRow>(BATCH_SIZE);

                // M11: 物理层发生在批事务之外（文件 IO 不长时间持锁；
                // 编码预留走独立事务）。失败的行回落旧版直存登记，
                // 保留元数据，不阻断整批导入。
                List<PreparedRow> prepared = batch
                    .Select(row => PrepareManaged(row, report))
                    .ToList();

                _db.RunInTransaction((conn, tx) =>
                {
                    foreach (PreparedRow item in prepared)
                    {
                        long newId = InsertImage(conn, tx, item);
                        idMap[item.Row.Id] = newId;
                        snapshot.Paths.Add(item.Row.FilePath);
                        snapshot.Hashes.Add(item.Row.Hash);
                        report.AddedCount++;
                    }
                });
                state.Added = report.AddedCount;
                state.Skipped = report.SkippedCount;
                progress?.Report(Clone(state));
            }

            foreach (LegacyRow row in rows)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Flush();
                    report.Cancelled = true;
                    break;
                }

                state.Processed++;
                state.CurrentFile = row.FilePath;
                progress?.Report(Clone(state));

                if (snapshot.Paths.Contains(row.FilePath))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（路径已存在）：{row.FilePath}");
                    continue;
                }
                if (snapshot.Hashes.Contains(row.Hash))
                {
                    report.SkippedCount++;
                    report.LogLines.Add($"跳过（内容重复）：{row.FileName}");
                    continue;
                }

                pending.Add(row);

                if (pending.Count >= BATCH_SIZE)
                    Flush();
            }

            Flush();

            if (idMap.Count > 0)
            {
                report.TagCount = ImportTags(legacy, idMap);
                report.CollectionCount = ImportCollections(legacy, idMap, report);
            }

            progress?.Report(Clone(state));
            return report;
        }
        catch (Exception ex)
        {
            report.ErrorMessage = $"导入失败：{ex.Message}";
            return report;
        }
    }

    private static bool TablesExist(SqliteConnection legacy)
    {
        using var command = legacy.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='images';";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private ExistingSnapshot LoadExisting() => _db.Run(conn =>
    {
        var snapshot = new ExistingSnapshot();
        using var command = conn.CreateCommand();
        // M11: 托管行的唯一路径是分类硬链接，外部原路径存 original_path，
        // 去重要同时覆盖两种行。
        command.CommandText =
            "SELECT COALESCE(original_path, file_path), file_hash FROM images;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshot.Paths.Add(reader.GetString(0));
            snapshot.Hashes.Add(reader.GetString(1));
        }
        return snapshot;
    });

    /// <summary>
    /// M11: reserves an entry code (own transaction), copies bytes into CAS
    /// and creates the category hardlink. Any failure — including a missing
    /// source file — falls back to legacy direct-path registration so the
    /// metadata row is never lost.
    /// </summary>
    private PreparedRow PrepareManaged(LegacyRow row, LegacyImportReport report)
    {
        if (_libraryRoot is null || !File.Exists(row.FilePath))
            return new PreparedRow(row, null, null, null);

        try
        {
            string entryCode = _db.RunInTransaction((conn, tx) =>
                EntryCodeGenerator.Next(
                    conn, tx, DateOnly.FromDateTime(DateTime.Now), row.Hash));

            string physicalPath = _storage.EnsurePhysicalFromFile(
                _libraryRoot, row.Hash, row.FilePath, entryCode);

            string hardlinkPath = LibraryRootResolver.GetCategoryHardlinkPath(
                _libraryRoot, row.Category, row.FileName, entryCode);
            _storage.CreateCategoryHardlink(physicalPath, hardlinkPath);

            return new PreparedRow(row, entryCode, physicalPath, hardlinkPath);
        }
        catch (Exception ex)
        {
            report.LogLines.Add(
                $"托管失败，按旧版路径登记：{row.FileName}（{ex.Message}）");
            return new PreparedRow(row, null, null, null);
        }
    }

    private static List<LegacyRow> LoadLegacyImages(SqliteConnection legacy, LegacyImportReport report)
    {
        var rows = new List<LegacyRow>();
        using var command = legacy.CreateCommand();
        command.CommandText = """
            SELECT id, file_path, file_name, file_hash, file_size,
                   width, height, format, rating, notes, primary_category, created_at
            FROM images
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string fileName = reader.GetString(2);
            string? category = reader.IsDBNull(10) ? null : reader.GetString(10);
            if (string.IsNullOrWhiteSpace(category))
                category = null; // '' → 未分类

            rows.Add(new LegacyRow(
                reader.GetInt64(0),
                reader.GetString(1),
                fileName,
                fileName.ToLowerInvariant(),
                reader.GetString(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                category,
                NormalizeTimestamp(reader.IsDBNull(11) ? null : reader.GetString(11))));
        }
        return rows;
    }

    private static long InsertImage(SqliteConnection conn, SqliteTransaction tx, PreparedRow item)
    {
        LegacyRow row = item.Row;
        bool managed = item.EntryCode is not null
            && item.PhysicalPath is not null
            && item.HardlinkPath is not null;

        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO images
                (file_path, file_name, sortable_name, file_hash, file_size,
                 width, height, format, rating, notes, primary_category,
                 dominant_colors, is_missing, created_at, updated_at,
                 library_hash, library_path, original_path, entry_code)
            VALUES
                ($path, $name, $sortable, $hash, $size,
                 $width, $height, $format, $rating, $notes, $category,
                 NULL, 0, $created, $created,
                 $libHash, $libPath, $origPath, $entryCode);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$path",
            managed ? item.HardlinkPath! : row.FilePath);
        command.Parameters.AddWithValue("$name", row.FileName);
        command.Parameters.AddWithValue("$sortable", row.SortableName);
        command.Parameters.AddWithValue("$hash", row.Hash);
        command.Parameters.AddWithValue("$size", row.FileSize);
        command.Parameters.AddNullable("$width", row.Width);
        command.Parameters.AddNullable("$height", row.Height);
        command.Parameters.AddNullable("$format", row.Format);
        command.Parameters.AddWithValue("$rating", row.Rating);
        command.Parameters.AddWithValue("$notes", row.Notes);
        command.Parameters.AddNullable("$category", row.Category);
        command.Parameters.AddWithValue("$created", row.CreatedAt);
        command.Parameters.AddNullable("$libHash", managed ? row.Hash : null);
        command.Parameters.AddNullable("$libPath",
            managed ? item.HardlinkPath : null);
        command.Parameters.AddNullable("$origPath",
            managed ? row.FilePath : null);
        command.Parameters.AddNullable("$entryCode",
            managed ? item.EntryCode : null);
        long newId = Convert.ToInt64(command.ExecuteScalar());

        if (managed)
        {
            using var lib = conn.CreateCommand();
            lib.Transaction = tx;
            lib.CommandText = """
                INSERT OR IGNORE INTO library_files
                    (content_hash, physical_path, file_size, width, height,
                     format, entry_code, created_at)
                VALUES
                    ($hash, $phys, $size, $width, $height,
                     $format, $code, $created);
                """;
            lib.Parameters.AddWithValue("$hash", row.Hash);
            lib.Parameters.AddWithValue("$phys", item.PhysicalPath);
            lib.Parameters.AddWithValue("$size", row.FileSize);
            lib.Parameters.AddNullable("$width", row.Width);
            lib.Parameters.AddNullable("$height", row.Height);
            lib.Parameters.AddNullable("$format", row.Format);
            lib.Parameters.AddWithValue("$code", item.EntryCode);
            lib.Parameters.AddWithValue("$created", row.CreatedAt);
            lib.ExecuteNonQuery();
        }

        return newId;
    }

    private int ImportTags(SqliteConnection legacy, Dictionary<long, long> idMap)
    {
        int added = 0;
        var pairs = new List<(long NewImageId, string Tag, string Source, double? Confidence)>();

        using (var command = legacy.CreateCommand())
        {
            command.CommandText = "SELECT image_id, tag, source, confidence FROM image_tags;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                long oldImageId = reader.GetInt64(0);
                if (!idMap.TryGetValue(oldImageId, out long newImageId))
                    continue; // skipped image: tags ride along only for imported ones

                string source = reader.IsDBNull(2) || reader.GetString(2) == "ai" ? "ai" : "manual";
                pairs.Add((newImageId, reader.GetString(1), source,
                    reader.IsDBNull(3) ? null : reader.GetDouble(3)));
            }
        }

        if (pairs.Count == 0) return 0;

        _db.RunInTransaction((conn, tx) =>
        {
            foreach ((long newImageId, string tag, string source, double? confidence) in pairs)
            {
                using var command = conn.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT OR IGNORE INTO image_tags (image_id, tag, source, confidence, created_at, pinyin)
                    VALUES ($image, $tag, $source, $confidence, $now, $pinyin);
                    """;
                command.Parameters.AddWithValue("$image", newImageId);
                command.Parameters.AddWithValue("$tag", tag);
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddNullable("$confidence", confidence);
                command.Parameters.AddWithValue("$now", DbTime.Now());
                command.Parameters.AddWithValue("$pinyin", PinyinHelper.GetPinyin(tag));
                added += command.ExecuteNonQuery();
            }
        });
        return added;
    }

    private int ImportCollections(SqliteConnection legacy, Dictionary<long, long> idMap, LegacyImportReport report)
    {
        var collectionMap = new Dictionary<long, long>();
        int added = 0;

        var legacyCollections = new List<(long OldId, string Name, string Description, long? OldCoverId, string CreatedAt)>();
        using (var command = legacy.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, description, cover_image_id, created_at
                FROM collections
                ORDER BY id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                legacyCollections.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    NormalizeTimestamp(reader.IsDBNull(4) ? null : reader.GetString(4))));
            }
        }

        foreach (var (oldId, name, description, oldCoverId, createdAt) in legacyCollections)
        {
            long? existing = GetCollectionIdByName(name);
            if (existing is { } reuseId)
            {
                collectionMap[oldId] = reuseId;
                report.LogLines.Add($"收藏夹「{name}」已存在，并入现有分组。");
                continue;
            }

            long? newCoverId = oldCoverId is { } c && idMap.TryGetValue(c, out long mapped) ? mapped : null;
            long newId = _db.RunInTransaction((conn, tx) =>
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO collections (name, description, cover_image_id, created_at, updated_at)
                    VALUES ($name, $desc, $cover, $created, $created);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$desc", description);
                insert.Parameters.AddNullable("$cover", newCoverId);
                insert.Parameters.AddWithValue("$created", createdAt);
                return Convert.ToInt64(insert.ExecuteScalar());
            });
            collectionMap[oldId] = newId;
            added++;
        }

        if (collectionMap.Count == 0) return 0;

        using (var command = legacy.CreateCommand())
        {
            command.CommandText =
                "SELECT collection_id, image_id, sort_order FROM collection_images;";
            using var reader = command.ExecuteReader();
            var pairs = new List<(long CollectionId, long ImageId, int SortOrder)>();
            while (reader.Read())
            {
                if (!collectionMap.TryGetValue(reader.GetInt64(0), out long newCollectionId))
                    continue;
                if (!idMap.TryGetValue(reader.GetInt64(1), out long newImageId))
                    continue;
                pairs.Add((newCollectionId, newImageId, reader.GetInt32(2)));
            }

            _db.RunInTransaction((conn, tx) =>
            {
                foreach ((long collectionId, long imageId, int sortOrder) in pairs)
                {
                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT OR IGNORE INTO collection_images (collection_id, image_id, sort_order)
                        VALUES ($cid, $iid, $order);
                        """;
                    insert.Parameters.AddWithValue("$cid", collectionId);
                    insert.Parameters.AddWithValue("$iid", imageId);
                    insert.Parameters.AddWithValue("$order", sortOrder);
                    insert.ExecuteNonQuery();
                }
            });
        }

        return added;
    }

    private long? GetCollectionIdByName(string name) => _db.Run<long?>(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT id FROM collections WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        object? result = command.ExecuteScalar();
        return result is null or DBNull ? (long?)null : Convert.ToInt64(result);
    });

    /// <summary>Python CURRENT_TIMESTAMP writes "yyyy-MM-dd HH:mm:ss" (UTC).</summary>
    private static string NormalizeTimestamp(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
        {
            return DbTime.Format(parsed);
        }
        return DbTime.Now();
    }

    private static LegacyImportProgress Clone(LegacyImportProgress state) => new()
    {
        ScannedTotal = state.ScannedTotal,
        Processed = state.Processed,
        Added = state.Added,
        Skipped = state.Skipped,
        CurrentFile = state.CurrentFile,
    };

    private sealed class ExistingSnapshot
    {
        public HashSet<string> Paths { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Hashes { get; } = new(StringComparer.Ordinal);
    }

    private sealed record LegacyRow(
        long Id,
        string FilePath,
        string FileName,
        string SortableName,
        string Hash,
        long FileSize,
        int? Width,
        int? Height,
        string? Format,
        int Rating,
        string Notes,
        string? Category,
        string CreatedAt);

    /// <summary>M11: per-row managed-storage artifacts; all null = legacy row.</summary>
    private sealed record PreparedRow(
        LegacyRow Row,
        string? EntryCode,
        string? PhysicalPath,
        string? HardlinkPath);
}
