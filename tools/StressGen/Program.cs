// M4-D 压测基建：合成素材库生成器（gen）+ SQL 层基线压测（bench）。
// 只使用 QL.World 的 public API（DatabaseService / ImageQueryService / PbrGroupingService /
// AppSettingsService / Categories / LibraryFilter），不复制主工程源码。
// 与主工程 AppDataRootResolver（internal）保持一致的字面量：库文件 qlworld.db、缩略图目录 thumbnails。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Pbr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace StressGen;

internal static class Program
{
    // AppDataRootResolver.DatabaseFileName / ThumbnailsFolderName 的实际值（internal 不可引用，用字面量对齐）。
    private const string DatabaseFileName = "qlworld.db";
    private const string ThumbnailsFolderName = "thumbnails";

    // 与主工程 DbTime（internal）相同的 ISO-8601 UTC 格式。
    private const string DbTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const int InsertBatchSize = 2000;
    private const int TagBatchSize = 5000;
    private const int MembersPerCollection = 500;
    private const int ThumbSize = 64;
    private const int ThumbQuality = 70;
    private const int ProgressStep = 10000;

    private static readonly string[] TagPool =
    [
        "风景", "夜景", "赛博朋克", "人像", "静物", "砖墙", "金属", "木头", "Fabric", "Stone",
        "日落", "海边", "森林", "雪山", "沙漠", "城市", "街拍", "建筑投影", "室内", "极简",
        "高对比", "柔光", "冷色调", "暖色调", "胶片", "黑白", "微距", "天空", "云海", "雨天",
        "雾气", "灯箱", "霓虹", "反射", "倒影", "纹理", "皮革", "玻璃", "混凝土", "锈迹",
        "苔藓", "树叶", "花卉", "食物", "静物台", "手绘", "概念稿", "板绘", "3D渲染", "低多边形",
        "体素", "像素画", "国风", "和风", "蒸汽波", "极光", "星空", "溪流", "峡谷", "城堡",
    ];

    private static readonly string[] PbrPrefixes =
        ["T_Wall", "T_Floor", "T_Rock", "T_Wood", "T_Metal", "T_Brick", "T_Sand", "T_Concrete"];

    private static readonly string[] PbrSuffixes = ["BaseColor", "Normal", "Roughness", "Metallic"];

    // 每个 set 目录固定 20 张贴图，按此尺寸切成 6 组（4+4+3+3+3+3 = 20），每组 ≥3 张同目录同基名。
    private static readonly int[] DirGroupSizes = [4, 4, 3, 3, 3, 3];

    private static readonly string[] CollectionNames =
        ["概念参考", "材质灵感", "场景氛围", "UI 灵感", "灯光研究"];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            string command = args[0].ToLowerInvariant();
            if (command is not ("gen" or "bench"))
            {
                PrintUsage();
                return 2;
            }

            var options = ParseOptions(args);
            return command == "gen" ? RunGen(options) : RunBench(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // ─── gen ───────────────────────────────────────────────────────────────

    private static int RunGen(IReadOnlyDictionary<string, string> options)
    {
        int count = GetInt(options, "count", 100000);
        string? outRoot = GetString(options, "out");
        if (count < 1 || string.IsNullOrWhiteSpace(outRoot))
        {
            Console.Error.WriteLine("[ERROR] gen 需要 --count >= 1 与 --out <dataRoot>。");
            return 2;
        }

        outRoot = Path.GetFullPath(outRoot);
        Directory.CreateDirectory(outRoot);
        string dbPath = Path.Combine(outRoot, DatabaseFileName);
        if (File.Exists(dbPath))
        {
            Console.Error.WriteLine($"[ERROR] {dbPath} 已存在；请先清空该数据根目录或换一个 --out。");
            return 1;
        }

        Console.WriteLine($"[gen] count={count:N0} out={outRoot}");
        var total = Stopwatch.StartNew();

        using var db = new DatabaseService(outRoot);
        db.Initialize();

        var settings = new AppSettingsService(outRoot);
        settings.Load();
        var grouping = new PbrGroupingService(db, settings);

        int textureCount = count * 5 / 100;
        int normalCount = count - textureCount;
        var createdAtBase = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        string thumbRoot = Path.Combine(outRoot, ThumbnailsFolderName);
        var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirGate = new object();
        string EnsureDir(string directory)
        {
            lock (dirGate)
            {
                if (knownDirs.Add(directory))
                    Directory.CreateDirectory(directory);
                return directory;
            }
        }
        string FormatTime(DateTime utc) => utc.ToString(DbTimeFormat, CultureInfo.InvariantCulture);

        // 阶段 1：并行生成真实源文件（PNG）+ 缩略图（WebP），按 2000 张一批参数化直插 images。
        var imageIds = new long[count];
        int doneRows = 0;
        var phase = Stopwatch.StartNew();
        Parallel.ForEach(Partitioner.Create(0, count, InsertBatchSize), range =>
        {
            var rows = new List<GenRow>(range.Item2 - range.Item1);
            for (int i = range.Item1; i < range.Item2; i++)
                rows.Add(GenerateRow(i, normalCount, outRoot, thumbRoot, createdAtBase, EnsureDir, FormatTime));

            db.RunInTransaction((conn, tx) =>
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
                         $width, $height, $format, $rating, $notes, $category,
                         $colors, $missing, $created, $updated);
                    SELECT last_insert_rowid();
                    """;
                var pPath = insert.Parameters.Add("$path", SqliteType.Text);
                var pName = insert.Parameters.Add("$name", SqliteType.Text);
                var pSortable = insert.Parameters.Add("$sortable", SqliteType.Text);
                var pHash = insert.Parameters.Add("$hash", SqliteType.Text);
                var pSize = insert.Parameters.Add("$size", SqliteType.Integer);
                var pWidth = insert.Parameters.Add("$width", SqliteType.Integer);
                var pHeight = insert.Parameters.Add("$height", SqliteType.Integer);
                var pFormat = insert.Parameters.Add("$format", SqliteType.Text);
                var pRating = insert.Parameters.Add("$rating", SqliteType.Integer);
                var pNotes = insert.Parameters.Add("$notes", SqliteType.Text);
                var pCategory = insert.Parameters.Add("$category", SqliteType.Text);
                var pColors = insert.Parameters.Add("$colors", SqliteType.Text);
                var pMissing = insert.Parameters.Add("$missing", SqliteType.Integer);
                var pCreated = insert.Parameters.Add("$created", SqliteType.Text);
                var pUpdated = insert.Parameters.Add("$updated", SqliteType.Text);

                foreach (GenRow row in rows)
                {
                    pPath.Value = row.FilePath;
                    pName.Value = row.FileName;
                    pSortable.Value = row.SortableName;
                    pHash.Value = row.Hash;
                    pSize.Value = row.Size;
                    pWidth.Value = 64;
                    pHeight.Value = 48;
                    pFormat.Value = "png";
                    pRating.Value = row.Rating;
                    pNotes.Value = string.Empty;
                    pCategory.Value = (object?)row.Category ?? DBNull.Value;
                    pColors.Value = DBNull.Value;
                    pMissing.Value = 0;
                    pCreated.Value = row.CreatedAt;
                    pUpdated.Value = row.CreatedAt;
                    imageIds[row.Index] = Convert.ToInt64(insert.ExecuteScalar());
                }
                return true;
            });

            int after = Interlocked.Add(ref doneRows, rows.Count);
            if (after / ProgressStep > (after - rows.Count) / ProgressStep)
                Console.WriteLine($"[gen] rows {after:N0}/{count:N0}  elapsed {phase.Elapsed.TotalSeconds:F1}s");
        });
        TimeSpan filesAndInsert = phase.Elapsed;

        // 阶段 2：普通图随机 2-4 个标签（贴图集图不打标签）。
        phase.Restart();
        int tagRowCount = 0;
        var pendingTags = new List<(long ImageId, string Tag, string Created)>(TagBatchSize);
        void FlushTags()
        {
            if (pendingTags.Count == 0)
                return;
            db.RunInTransaction((conn, tx) =>
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT OR IGNORE INTO image_tags (image_id, tag, source, confidence, created_at)
                    VALUES ($id, $tag, 'manual', 1.0, $created);
                    """;
                var pId = insert.Parameters.Add("$id", SqliteType.Integer);
                var pTag = insert.Parameters.Add("$tag", SqliteType.Text);
                var pCreated = insert.Parameters.Add("$created", SqliteType.Text);
                foreach (var entry in pendingTags)
                {
                    pId.Value = entry.ImageId;
                    pTag.Value = entry.Tag;
                    pCreated.Value = entry.Created;
                    insert.ExecuteNonQuery();
                }
                return true;
            });
            tagRowCount += pendingTags.Count;
            pendingTags.Clear();
        }

        for (int i = 0; i < normalCount; i++)
        {
            int tagTotal = 2 + Random.Shared.Next(3);
            var chosen = new HashSet<int>();
            while (chosen.Count < tagTotal)
                chosen.Add(Random.Shared.Next(TagPool.Length));
            string created = FormatTime(createdAtBase.AddSeconds(i));
            foreach (int t in chosen)
                pendingTags.Add((imageIds[i], TagPool[t], created));
            if (pendingTags.Count >= TagBatchSize)
                FlushTags();
        }
        FlushTags();
        TimeSpan tagsTime = phase.Elapsed;

        // 阶段 3：5 个收藏夹，各随机加入最多 500 张。
        phase.Restart();
        var collectionIds = new long[CollectionNames.Length];
        db.RunInTransaction((conn, tx) =>
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO collections (name, cover_image_id, created_at, updated_at)
                VALUES ($name, $cover, $created, $updated);
                SELECT last_insert_rowid();
                """;
            var pName = insert.Parameters.Add("$name", SqliteType.Text);
            var pCover = insert.Parameters.Add("$cover", SqliteType.Integer);
            var pCreated = insert.Parameters.Add("$created", SqliteType.Text);
            var pUpdated = insert.Parameters.Add("$updated", SqliteType.Text);
            string now = FormatTime(DateTime.UtcNow);
            for (int c = 0; c < CollectionNames.Length; c++)
            {
                pName.Value = CollectionNames[c];
                pCover.Value = imageIds[Random.Shared.Next(count)];
                pCreated.Value = now;
                pUpdated.Value = now;
                collectionIds[c] = Convert.ToInt64(insert.ExecuteScalar());
            }
            return true;
        });

        int membersPer = Math.Min(MembersPerCollection, count);
        foreach (long collectionId in collectionIds)
        {
            var chosen = new HashSet<int>();
            while (chosen.Count < membersPer)
                chosen.Add(Random.Shared.Next(count));
            db.RunInTransaction((conn, tx) =>
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO collection_images (collection_id, image_id, sort_order)
                    VALUES ($cid, $iid, $order);
                    """;
                var pCid = insert.Parameters.Add("$cid", SqliteType.Integer);
                var pIid = insert.Parameters.Add("$iid", SqliteType.Integer);
                var pOrder = insert.Parameters.Add("$order", SqliteType.Integer);
                int order = 0;
                foreach (int index in chosen)
                {
                    pCid.Value = collectionId;
                    pIid.Value = imageIds[index];
                    pOrder.Value = order++;
                    insert.ExecuteNonQuery();
                }
                return true;
            });
        }
        TimeSpan collectionsTime = phase.Elapsed;

        // 阶段 4：主工程 PBR 分组服务重扫，生成贴图组。
        phase.Restart();
        PbrRescanReport pbrReport = grouping.RescanAll();
        TimeSpan pbrTime = phase.Elapsed;

        // 收尾统计。
        phase.Restart();
        int dbCount = db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM images;";
            return Convert.ToInt32(command.ExecuteScalar());
        });
        long rootBytes = DirectorySize(outRoot);
        int fileCount = Directory.EnumerateFiles(outRoot, "*", SearchOption.AllDirectories).Count();
        TimeSpan statTime = phase.Elapsed;
        total.Stop();

        Console.WriteLine();
        Console.WriteLine("[gen] ===== 统计 =====");
        Console.WriteLine($"[gen] images        : {dbCount:N0} (normal {normalCount:N0} + texture {textureCount:N0})");
        Console.WriteLine($"[gen] tag rows      : {tagRowCount:N0} (pool {TagPool.Length} 词, 每图 2-4)");
        Console.WriteLine($"[gen] collections   : {CollectionNames.Length} × {membersPer:N0}");
        Console.WriteLine($"[gen] pbr rescan    : scanned {pbrReport.ImagesScanned:N0}, groups {pbrReport.GroupsCreated:N0}, members {pbrReport.MembersLinked:N0}");
        Console.WriteLine($"[gen] phase timings : files+insert {filesAndInsert.TotalSeconds:F1}s, tags {tagsTime.TotalSeconds:F1}s, collections {collectionsTime.TotalSeconds:F1}s, pbr {pbrTime.TotalSeconds:F1}s, stats {statTime.TotalSeconds:F1}s");
        Console.WriteLine($"[gen] TOTAL         : {total.Elapsed.TotalMinutes:F2} min ({total.Elapsed.TotalSeconds:F1}s), {count / Math.Max(total.Elapsed.TotalSeconds, 0.001):F0} images/s");
        Console.WriteLine($"[gen] root size     : {rootBytes / 1048576.0:F1} MB across {fileCount:N0} files");
        Console.WriteLine($"[gen] database      : {dbPath}");

        return dbCount == count ? 0 : 1;
    }

    private static GenRow GenerateRow(
        int i,
        int normalCount,
        string outRoot,
        string thumbRoot,
        DateTime createdAtBase,
        Func<string, string> ensureDir,
        Func<DateTime, string> formatTime)
    {
        bool isTexture = i >= normalCount;
        string fileName;
        string filePath;
        string? category;

        if (!isTexture)
        {
            // 95% 普通图：photo_{i:D6}.png，64×48，10 个固定分类轮转。
            fileName = $"photo_{i:D6}.png";
            filePath = Path.Combine(outRoot, fileName);
            category = Categories.All[i % Categories.All.Count];
        }
        else
        {
            // 5% 贴图集：textures\set_{k:D4}\，k = i/20，每组 3-4 张同目录同基名。
            int dirK = i / 20;
            int ordinal = i - dirK * 20;
            (int groupIdx, int memberIdx) = LocateGroup(ordinal);
            string baseName = $"{PbrPrefixes[groupIdx % PbrPrefixes.Length]}_{dirK:D4}_{groupIdx:D2}";
            fileName = $"{baseName}_{PbrSuffixes[memberIdx]}.png";
            filePath = Path.Combine(outRoot, "textures", $"set_{dirK:D4}", fileName);
            category = Categories.All[0];
        }

        string hash = NewHash();
        var color = new Rgba32((byte)(i * 7 % 256), (byte)(i * 13 % 256), (byte)(i * 29 % 256));

        ensureDir(Path.GetDirectoryName(filePath)!);
        using (var image = new Image<Rgba32>(64, 48))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                    accessor.GetRowSpan(y).Fill(color);
            });
            image.SaveAsPng(filePath);

            // 缩略图布局与 ThumbnailService.GetThumbnailPath 一致：thumbnails\<hash[..2]>\<hash>.webp。
            string thumbPath = Path.Combine(thumbRoot, hash[..2], $"{hash}.webp");
            ensureDir(Path.GetDirectoryName(thumbPath)!);
            image.SaveAsWebp(thumbPath, new WebpEncoder { Quality = ThumbQuality });
        }

        long size = new FileInfo(filePath).Length;
        string createdAt = formatTime(createdAtBase.AddSeconds(i));
        return new GenRow(
            i, filePath, fileName, fileName.ToLowerInvariant(), hash, size,
            category, Random.Shared.Next(6), createdAt);
    }

    private static (int Group, int Member) LocateGroup(int ordinal)
    {
        int accumulated = 0;
        for (int g = 0; g < DirGroupSizes.Length; g++)
        {
            if (ordinal < accumulated + DirGroupSizes[g])
                return (g, ordinal - accumulated);
            accumulated += DirGroupSizes[g];
        }

        // 20 张以内不会走到这里；兜底归入最后一组。
        return (DirGroupSizes.Length - 1, Math.Max(0, ordinal - accumulated + DirGroupSizes[^1]));
    }

    private static string NewHash()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static long DirectorySize(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

    private sealed record GenRow(
        int Index,
        string FilePath,
        string FileName,
        string SortableName,
        string Hash,
        long Size,
        string? Category,
        int Rating,
        string CreatedAt);

    // ─── bench ─────────────────────────────────────────────────────────────

    private static int RunBench(IReadOnlyDictionary<string, string> options)
    {
        string? root = GetString(options, "root");
        int runs = Math.Max(1, GetInt(options, "runs", 5));
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.Error.WriteLine("[ERROR] bench 需要 --root <dataRoot>（已存在的数据根）。");
            return 2;
        }
        root = Path.GetFullPath(root);

        using var db = new DatabaseService(root);
        db.Initialize();

        var settings = new AppSettingsService(root);
        settings.Load();
        var query = new ImageQueryService(db);
        var grouping = new PbrGroupingService(db, settings);

        // 基准上下文：最热门标签 / 第一个收藏夹 / 第一个贴图组。
        string hotTag = db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText =
                "SELECT tag FROM image_tags GROUP BY tag ORDER BY COUNT(*) DESC LIMIT 1;";
            return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        });
        long? collectionId = db.Run<long?>(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT id FROM collections ORDER BY id LIMIT 1;";
            object? result = command.ExecuteScalar();
            return result is null or DBNull ? null : Convert.ToInt64(result);
        });
        var groups = grouping.GetGroups();
        long? pbrGroupId = groups.Count > 0 ? groups[0].Id : null;

        Console.WriteLine($"[bench] root={root} runs={runs}");
        Console.WriteLine($"[bench] hotTag='{hotTag}' collectionId={collectionId?.ToString() ?? "-"} "
            + $"pbrGroupId={pbrGroupId?.ToString() ?? "-"} (groups={groups.Count})");

        var ops = new List<BenchOp>
        {
            new("1 QueryGallery 全量 (ImportedDesc)",
                () => query.QueryGallery(new LibraryFilter()).Count),
            new("2 QueryGallery Text='photo_0' (FileNameNotes)",
                () => query.QueryGallery(new LibraryFilter { Text = "photo_0", SearchMode = SearchMode.FileNameNotes }).Count),
            new($"3 QueryGallery Tag='{hotTag}'",
                () => query.QueryGallery(new LibraryFilter { Tag = hotTag }).Count),
            new($"4 QueryGallery Category='{Categories.All[0]}'",
                () => query.QueryGallery(new LibraryFilter { Category = Categories.All[0] }).Count),
        };
        if (pbrGroupId is long gid)
            ops.Add(new($"5 QueryGallery PbrGroupId={gid}",
                () => query.QueryGallery(new LibraryFilter { PbrGroupId = gid }).Count));
        if (collectionId is long cid)
            ops.Add(new($"6 QueryGallery CollectionId={cid}",
                () => query.QueryGallery(new LibraryFilter { CollectionId = cid }).Count));
        ops.Add(new("7 GetCategoryCounts", () => query.GetCategoryCounts().Count));
        ops.Add(new("8 PbrGroupingService.GetGroups", () => grouping.GetGroups().Count));
        ops.Add(new("9 raw SELECT COUNT(*) FROM images", () => db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM images;";
            return Convert.ToInt32(command.ExecuteScalar());
        })));
        // M4-D 分页链路：RefreshAsync（首页 + 过滤计数）与深翻页（OFFSET 50k）。
        ops.Add(new("10 分页首页 QueryGallery{Limit=1000}+CountFiltered", () =>
        {
            var filter = new LibraryFilter { Limit = 1000, Offset = 0 };
            int rows = query.QueryGallery(filter).Count;
            return rows + query.CountFiltered(filter);
        }));
        ops.Add(new("11 深翻页 QueryGallery{Limit=1000, Offset=50000}+CountFiltered", () =>
        {
            var filter = new LibraryFilter { Limit = 1000, Offset = 50000 };
            int rows = query.QueryGallery(filter).Count;
            return rows + query.CountFiltered(filter);
        }));

        var results = new List<BenchResult>(ops.Count);
        foreach (BenchOp op in ops)
        {
            var times = new double[runs];
            int rows = 0;
            for (int r = 0; r < runs; r++)
            {
                var watch = Stopwatch.StartNew();
                rows = op.Execute();
                watch.Stop();
                times[r] = watch.Elapsed.TotalMilliseconds;
            }
            double median = Median(times);
            double max = times.Max();
            results.Add(new BenchResult(op.Name, rows, times[0], median, max));
            Console.WriteLine(
                $"  done: {op.Name,-46} rows={rows,7:N0} first={times[0],9:F1}ms median={median,9:F1}ms max={max,9:F1}ms");
        }

        Console.WriteLine();
        Console.WriteLine("[bench] 汇总表");
        Console.WriteLine("| 操作 | 行数 | 首次 ms | 中位 ms | 最大 ms |");
        Console.WriteLine("|---|---:|---:|---:|---:|");
        foreach (BenchResult result in results)
            Console.WriteLine(
                $"| {result.Name} | {result.Rows:N0} | {result.FirstMs:F1} | {result.MedianMs:F1} | {result.MaxMs:F1} |");

        // QueryGallery 内部 SQL 不可直接取：按同样语义手写代表性 SQL 打查询计划。
        Console.WriteLine();
        Console.WriteLine("[bench] EXPLAIN QUERY PLAN");
        Explain(db, "op1 全量 (ImportedDesc)",
            "SELECT id FROM images ORDER BY created_at DESC, id DESC;", []);
        Explain(db, "op10 分页首页 (ImportedDesc LIMIT 1000)",
            "SELECT id FROM images ORDER BY created_at DESC, id DESC LIMIT 1000 OFFSET 0;", []);
        Explain(db, "op3 标签过滤", """
            SELECT i.id FROM images i
            WHERE EXISTS (SELECT 1 FROM image_tags it
                          WHERE it.image_id = i.id AND it.tag = $tag);
            """, [("$tag", hotTag)]);
        if (pbrGroupId is long pgid)
            Explain(db, "op5 贴图组过滤", """
                SELECT i.id FROM images i
                WHERE EXISTS (SELECT 1 FROM pbr_group_images pgi
                              WHERE pgi.image_id = i.id AND pgi.group_id = $g);
                """, [("$g", pgid)]);

        return 0;
    }

    private static double Median(double[] values)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static void Explain(
        DatabaseService db,
        string title,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters)
    {
        Console.WriteLine($"-- {title}");
        db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                Console.WriteLine($"   ({reader.GetInt64(0)},{reader.GetInt64(1)}) {reader.GetString(3)}");
        });
    }

    private sealed record BenchOp(string Name, Func<int> Execute);

    private sealed record BenchResult(string Name, int Rows, double FirstMs, double MedianMs, double MaxMs);

    // ─── CLI ───────────────────────────────────────────────────────────────

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            用法:
              dotnet run --project tools/StressGen -c Release -- gen --count 100000 --out <dataRoot>
              dotnet run --project tools/StressGen -c Release -- bench --root <dataRoot> [--runs 5]
            """);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            string key = arg[2..];
            string value;
            int equals = key.IndexOf('=');
            if (equals >= 0)
            {
                value = key[(equals + 1)..];
                key = key[..equals];
            }
            else if (i + 1 < args.Length)
            {
                value = args[++i];
            }
            else
            {
                continue;
            }
            result[key] = value;
        }
        return result;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> options, string key, int fallback) =>
        options.TryGetValue(key, out string? raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static string? GetString(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) ? value : null;
}
