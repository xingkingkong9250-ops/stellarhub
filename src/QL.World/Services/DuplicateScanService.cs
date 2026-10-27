using System.IO;
using QL.World.Data;
using QL.World.Services.Ai;

namespace QL.World.Services;

/// <summary>0.16.0 DUP-C: 全库查重扫描阶段（供进度条与日志展示）。</summary>
public enum DuplicateScanPhase
{
    /// <summary>从 256px 缩略图补算存量 phash。</summary>
    Backfill,
    /// <summary>BK-tree 半径查询，生成 L1 严格同图候选对。</summary>
    PhashPairing,
    /// <summary>按 model_id 分组的分块余弦扫描，生成 L2 近似变体候选对。</summary>
    VectorScan,
    /// <summary>0.16.0 DUP-E: 模糊边界带候选对送云端 VLM 终审。</summary>
    TerminalReview,
    /// <summary>聚类并把候选组写入 duplicate_candidates。</summary>
    Persist,
}

/// <summary>0.16.0 DUP-C: 扫描进度快照。</summary>
public sealed class DuplicateScanProgress
{
    public DuplicateScanPhase Phase { get; init; }
    public int Processed { get; init; }
    public int Total { get; init; }

    /// <summary>当前已收集的候选对数（仅用于日志/诊断）。</summary>
    public int CandidatePairs { get; init; }
}

/// <summary>0.16.0 DUP-C: 一次全库扫描的结果统计。</summary>
public sealed class DuplicateScanResult
{
    /// <summary>本轮从缩略图成功补算并写回的 phash 数。</summary>
    public int PhashBackfilled { get; set; }

    /// <summary>扫描结束时库内已有 phash 的图片数。</summary>
    public int ImagesWithPhash { get; set; }

    /// <summary>仍无 phash（无缩略图/解码失败）的图片数。</summary>
    public int ImagesWithoutPhash { get; set; }

    /// <summary>参与 L2 余弦扫描的向量数。</summary>
    public int VectorCount { get; set; }

    /// <summary>候选组总数。</summary>
    public int GroupCount { get; set; }

    /// <summary>全员 L1 同图的组数。</summary>
    public int ExactGroupCount { get; set; }

    /// <summary>含 L2 近似变体成员的组数。</summary>
    public int VariantGroupCount { get; set; }

    /// <summary>写入 duplicate_candidates 的成员行数（每图一行）。</summary>
    public int CandidateCount { get; set; }

    /// <summary>0.16.0 DUP-E: 本次扫描是否具备云端 VLM 终审能力。</summary>
    public bool L3Available { get; set; }

    /// <summary>0.16.0 DUP-E: 送入 VLM 终审的边界带候选对数。</summary>
    public int L3PairsReviewed { get; set; }

    /// <summary>0.16.0 DUP-E: VLM 确认同图、以 L3 入组的候选对数。</summary>
    public int L3ConfirmedPairs { get; set; }

    /// <summary>0.16.0 DUP-E: VLM 否决而丢弃的候选对数。</summary>
    public int L3RejectedPairs { get; set; }

    /// <summary>0.16.0 DUP-E: 含 L3 终审成员的组数。</summary>
    public int L3GroupCount { get; set; }

    /// <summary>用户取消：候选表未写入。</summary>
    public bool Cancelled { get; set; }
}

/// <summary>
/// 0.16.0 DUP-C: 全库近重复扫描。
/// L1 = 64 位 dHash + 内存 BK-tree（汉明距离 ≤10，严格同图）；
/// L2 = image_embeddings 同 model_id 分块余弦（≥0.92，近似变体）；
/// L3 = 0.16.0 DUP-E：pHash 距离 11~18 或余弦 0.88~0.92 的边界带候选对，
///      云端 VLM 可用时双图终审，确认才以 level='L3' 入组，否决丢弃；
///      不可用静默跳过（结果与仅 L1/L2 时一致）。
/// 候选对取传递闭包成组，每组成员各写一行 duplicate_candidates（pending）。
/// 新一轮扫描先清 pending 旧行，resolved/dismissed 历史保留；取消时不写候选表。
/// 存量 phash 缺失时按需从 &lt;缩略图根&gt;\&lt;hash2&gt;\&lt;hash&gt;_thumb.webp 补算。
/// </summary>
public sealed class DuplicateScanService
{
    /// <summary>L2 近似变体阈值：同 model_id 内余弦 ≥0.92 视为同一画面变体。</summary>
    public const double VariantMinCosine = 0.92;

    /// <summary>0.16.0 DUP-E: 模糊边界带余弦下限（0.88~0.92 交给 VLM 终审）。</summary>
    public const double BoundaryMinCosine = 0.88;

    /// <summary>0.16.0 DUP-E: 模糊边界带 pHash 汉明距离上限（11~18 交给 VLM 终审）。</summary>
    public const int BoundaryMaxDistance = 18;

    /// <summary>L2 分块大小：块内与跨块成对比较，控制单次工作集。</summary>
    private const int VECTOR_CHUNK_SIZE = 256;

    /// <summary>L1 配对进度上报间隔（张）。</summary>
    private const int L1_PROGRESS_INTERVAL = 64;

    private readonly DatabaseService _db;
    private readonly PerceptualHashService _hasher;
    private readonly string? _thumbnailRoot;
    private readonly IVlmImageCompare? _vlm;

    public DuplicateScanService(
        DatabaseService db,
        PerceptualHashService? hasher = null,
        string? thumbnailRoot = null,
        IVlmImageCompare? vlm = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hasher = hasher ?? new PerceptualHashService();
        _thumbnailRoot = string.IsNullOrWhiteSpace(thumbnailRoot) ? null : thumbnailRoot;
        _vlm = vlm;
    }

    /// <summary>
    /// 后台执行一次全库扫描。令牌只被扫描核心逐阶段检查（不传给
    /// Task.Run，避免预取消时委托完全不执行而拿不到 Cancelled 结果）。
    /// </summary>
    public Task<DuplicateScanResult> ScanAsync(
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanCoreAsync(progress, cancellationToken));

    private async Task<DuplicateScanResult> ScanCoreAsync(
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new DuplicateScanResult();

        BackfillPhashes(result, progress, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            result.Cancelled = true;
            return result;
        }

        List<PhashRow> phashes = LoadPhashes();
        result.ImagesWithPhash = phashes.Count;
        result.ImagesWithoutPhash = CountImages() - phashes.Count;

        var tree = new PhashBkTree();
        foreach (PhashRow row in phashes)
            tree.Add(row.ImageId, row.Hash);

        var pairs = new Dictionary<(long A, long B), CandidatePair>();
        var boundary = new Dictionary<(long A, long B), CandidatePair>();
        CollectL1Pairs(phashes, tree, pairs, boundary, progress, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            result.Cancelled = true;
            return result;
        }

        result.VectorCount = CollectL2Pairs(pairs, boundary, progress, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            result.Cancelled = true;
            return result;
        }

        if (boundary.Count > 0 && _vlm is not null)
        {
            result.L3Available = true;
            await ReviewBoundaryAsync(boundary, pairs, result, progress, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }
        }

        List<CandidateGroup> groups = BuildGroups(pairs);
        result.GroupCount = groups.Count;
        result.ExactGroupCount = groups.Count(group => group.AllExact);
        result.VariantGroupCount = result.GroupCount - result.ExactGroupCount;
        result.CandidateCount = groups.Sum(group => group.Members.Count);
        result.L3GroupCount = groups.Count(group =>
            group.Members.Any(member => member.Level == "L3"));

        Persist(groups);
        progress?.Report(new DuplicateScanProgress
        {
            Phase = DuplicateScanPhase.Persist,
            Processed = 1,
            Total = 1,
            CandidatePairs = pairs.Count,
        });
        return result;
    }

    // ── phash 补算 ────────────────────────────────────────────────────

    private void BackfillPhashes(
        DuplicateScanResult result,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<(long Id, string FileHash)> rows = _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT id, file_hash FROM images
                WHERE (phash IS NULL OR phash = '') AND file_hash IS NOT NULL
                ORDER BY id;
                """;
            var list = new List<(long, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetString(1)));
            return list;
        });

        if (rows.Count == 0)
            return;

        progress?.Report(new DuplicateScanProgress
        {
            Phase = DuplicateScanPhase.Backfill,
            Processed = 0,
            Total = rows.Count,
        });

        // 未配置缩略图根目录（测试/无 UI 场景）时跳过补算，已有 phash 照常扫描。
        if (_thumbnailRoot is null)
            return;

        int processed = 0;
        foreach ((long id, string fileHash) in rows)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            string? phash = TryComputeFromThumbnail(fileHash);
            if (phash is not null)
            {
                _db.Run(conn =>
                {
                    using var command = conn.CreateCommand();
                    command.CommandText =
                        "UPDATE images SET phash = $phash WHERE id = $id AND (phash IS NULL OR phash = '');";
                    command.Parameters.AddWithValue("$phash", phash);
                    command.Parameters.AddWithValue("$id", id);
                    command.ExecuteNonQuery();
                });
                result.PhashBackfilled++;
            }

            processed++;
            progress?.Report(new DuplicateScanProgress
            {
                Phase = DuplicateScanPhase.Backfill,
                Processed = processed,
                Total = rows.Count,
            });
        }
    }

    /// <summary>按 ThumbnailService 的 256px 布局定位并计算 phash；缺失返回 null。</summary>
    private string? TryComputeFromThumbnail(string fileHash)
    {
        string? path = FindThumbnailPath(fileHash);
        if (path is null)
            return null;

        return _hasher.TryCompute(path) is ulong hash
            ? PerceptualHashService.ToHex(hash)
            : null;
    }

    /// <summary>缓存缩略图路径（&lt;根&gt;\&lt;hash2&gt;\&lt;hash&gt;_thumb.webp）；缺失返回 null。</summary>
    private string? FindThumbnailPath(string fileHash)
    {
        if (_thumbnailRoot is null || string.IsNullOrWhiteSpace(fileHash))
            return null;

        string key = fileHash.ToLowerInvariant();
        if (key.Length < 2)
            return null;

        string path = Path.Combine(_thumbnailRoot, key[..2], $"{key}_thumb.webp");
        return File.Exists(path) ? path : null;
    }

    // ── L1：BK-tree 半径配对 ─────────────────────────────────────────

    private static void CollectL1Pairs(
        List<PhashRow> phashes,
        PhashBkTree tree,
        Dictionary<(long A, long B), CandidatePair> pairs,
        Dictionary<(long A, long B), CandidatePair> boundary,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var hits = new List<(long ImageId, int Distance)>();
        int total = phashes.Count;
        for (int i = 0; i < total; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            PhashRow row = phashes[i];
            hits.Clear();
            // 一次半径查询同时取 L1（≤10）与 L3 边界带（11~18）。
            tree.Search(row.Hash, BoundaryMaxDistance, row.ImageId, hits);
            foreach ((long otherId, int distance) in hits)
            {
                if (otherId == row.ImageId)
                    continue;
                // 距离取倒数转为相似度；同图距离越小得分越高。
                double score = 1.0 / (1.0 + distance);
                if (distance <= PerceptualHashService.SameImageMaxDistance)
                    AddPair(pairs, row.ImageId, otherId, exact: true, score, "L1");
                else
                    AddPair(boundary, row.ImageId, otherId, exact: true, score, "L3");
            }

            int processed = i + 1;
            if (processed % L1_PROGRESS_INTERVAL == 0 || processed == total)
            {
                progress?.Report(new DuplicateScanProgress
                {
                    Phase = DuplicateScanPhase.PhashPairing,
                    Processed = processed,
                    Total = total,
                    CandidatePairs = pairs.Count,
                });
            }
        }
    }

    // ── L2：同 model_id 分块余弦 ─────────────────────────────────────

    /// <summary>返回参与扫描的向量总数（跨所有 model_id）。</summary>
    private int CollectL2Pairs(
        Dictionary<(long A, long B), CandidatePair> pairs,
        Dictionary<(long A, long B), CandidatePair> boundary,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<VectorEntry>> byModel = LoadEmbeddings();
        int vectorCount = byModel.Values.Sum(list => list.Count);
        if (vectorCount == 0)
            return 0;

        int totalBlockPairs = 0;
        var blocksByModel = new List<List<List<VectorEntry>>>(byModel.Count);
        foreach (List<VectorEntry> vectors in byModel.Values)
        {
            List<List<VectorEntry>> blocks = Chunk(vectors);
            blocksByModel.Add(blocks);
            totalBlockPairs += blocks.Count * (blocks.Count + 1) / 2;
        }

        int completed = 0;
        foreach (List<List<VectorEntry>> blocks in blocksByModel)
        {
            for (int a = 0; a < blocks.Count; a++)
            {
                for (int b = a; b < blocks.Count; b++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return vectorCount;

                    CompareBlocks(blocks[a], blocks[b], sameBlock: a == b, pairs, boundary);
                    completed++;
                    progress?.Report(new DuplicateScanProgress
                    {
                        Phase = DuplicateScanPhase.VectorScan,
                        Processed = completed,
                        Total = totalBlockPairs,
                        CandidatePairs = pairs.Count,
                    });
                }
            }
        }
        return vectorCount;
    }

    private Dictionary<string, List<VectorEntry>> LoadEmbeddings() => _db.Run(conn =>
    {
        // 同 model_id 的向量空间才可互比；local/cloud 不得跨模型配对。
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT image_id, model_id, dim, embedding
            FROM image_embeddings
            WHERE dim > 0
            ORDER BY model_id, image_id;
            """;

        var byModel = new Dictionary<string, List<VectorEntry>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long imageId = reader.GetInt64(0);
            string modelId = reader.IsDBNull(1) ? "none" : reader.GetString(1);
            int dim = reader.GetInt32(2);
            if (reader.IsDBNull(3))
                continue;

            byte[] blob = (byte[])reader[3];
            if (dim <= 0 || blob.Length != dim * sizeof(float))
                continue;

            var vector = new float[dim];
            Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
            double norm = 0;
            foreach (float value in vector)
                norm += value * value;
            if (norm <= 0)
                continue;

            if (!byModel.TryGetValue(modelId, out List<VectorEntry>? list))
            {
                list = [];
                byModel[modelId] = list;
            }
            list.Add(new VectorEntry(imageId, vector, Math.Sqrt(norm)));
        }
        return byModel;
    });

    private static List<List<VectorEntry>> Chunk(List<VectorEntry> vectors)
    {
        var blocks = new List<List<VectorEntry>>();
        for (int start = 0; start < vectors.Count; start += VECTOR_CHUNK_SIZE)
        {
            int count = Math.Min(VECTOR_CHUNK_SIZE, vectors.Count - start);
            blocks.Add(vectors.GetRange(start, count));
        }
        return blocks;
    }

    private static void CompareBlocks(
        List<VectorEntry> first,
        List<VectorEntry> second,
        bool sameBlock,
        Dictionary<(long A, long B), CandidatePair> pairs,
        Dictionary<(long A, long B), CandidatePair> boundary)
    {
        for (int i = 0; i < first.Count; i++)
        {
            VectorEntry a = first[i];
            int start = sameBlock ? i + 1 : 0;
            for (int j = start; j < second.Count; j++)
            {
                VectorEntry b = second[j];
                if (a.ImageId == b.ImageId)
                    continue;
                if (a.Vector.Length != b.Vector.Length)
                    continue;

                double dot = 0;
                for (int k = 0; k < a.Vector.Length; k++)
                    dot += a.Vector[k] * (double)b.Vector[k];
                double similarity = dot / (a.Norm * b.Norm);
                if (similarity < BoundaryMinCosine)
                    continue;

                bool definite = similarity >= VariantMinCosine;
                (long, long) key = a.ImageId < b.ImageId
                    ? (a.ImageId, b.ImageId)
                    : (b.ImageId, a.ImageId);
                if (definite)
                {
                    // 余弦已过 L2 阈值：确定为变体，边界带不再重复送审。
                    AddPair(pairs, a.ImageId, b.ImageId, exact: false, Math.Min(1.0, similarity), "L2");
                    boundary.Remove(key);
                }
                else
                {
                    AddPair(boundary, a.ImageId, b.ImageId, exact: false, Math.Min(1.0, similarity), "L3");
                }
            }
        }
    }

    private static void AddPair(
        Dictionary<(long A, long B), CandidatePair> pairs,
        long first,
        long second,
        bool exact,
        double score,
        string level)
    {
        (long, long) key = first < second ? (first, second) : (second, first);
        if (pairs.TryGetValue(key, out CandidatePair? existing))
        {
            if (PairRank(level) > PairRank(existing.Level) ||
                (PairRank(level) == PairRank(existing.Level) && score > existing.Score))
                pairs[key] = new CandidatePair(exact, score, level);
            return;
        }
        pairs[key] = new CandidatePair(exact, score, level);
    }

    /// <summary>层级优先级：L1 确定同图 &gt; L3 已终审 &gt; L2 近似变体。</summary>
    private static int PairRank(string level) => level switch
    {
        "L1" => 3,
        "L3" => 2,
        _ => 1,
    };

    // ── L3：边界带 VLM 终审 ─────────────────────────────────────────

    /// <summary>
    /// 逐对送云端 VLM 终审边界带候选：确认以 level='L3' 入组，否决丢弃。
    /// 无本地缩略图的远程图或 VLM 无判定（Confidence&lt;=0）时跳过该对，
    /// 不中断扫描；取消时立即返回、由调用方放弃写表。
    /// </summary>
    private async Task ReviewBoundaryAsync(
        Dictionary<(long A, long B), CandidatePair> boundary,
        Dictionary<(long A, long B), CandidatePair> pairs,
        DuplicateScanResult result,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        Dictionary<long, CompareSource> images = LoadCompareSources();
        List<KeyValuePair<(long A, long B), CandidatePair>> reviewable = boundary
            .Where(entry => !pairs.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key.A)
            .ThenBy(entry => entry.Key.B)
            .ToList();

        int total = reviewable.Count;
        progress?.Report(new DuplicateScanProgress
        {
            Phase = DuplicateScanPhase.TerminalReview,
            Processed = 0,
            Total = total,
            CandidatePairs = pairs.Count,
        });

        int processed = 0;
        foreach (KeyValuePair<(long A, long B), CandidatePair> entry in reviewable)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            (long a, long b) = entry.Key;
            string? pathA = ResolveComparePath(images, a);
            string? pathB = ResolveComparePath(images, b);
            if (pathA is null || pathB is null)
                continue;

            (bool same, double confidence) verdict;
            try
            {
                verdict = await _vlm!.CompareImagesAsync(pathA, pathB, cancellationToken);
            }
            catch
            {
                verdict = (false, 0);
            }

            result.L3PairsReviewed++;
            if (verdict.confidence > 0)
            {
                if (verdict.same)
                {
                    AddPair(pairs, a, b, entry.Value.Exact, entry.Value.Score, "L3");
                    result.L3ConfirmedPairs++;
                }
                else
                {
                    result.L3RejectedPairs++;
                }
            }

            processed++;
            progress?.Report(new DuplicateScanProgress
            {
                Phase = DuplicateScanPhase.TerminalReview,
                Processed = processed,
                Total = total,
                CandidatePairs = pairs.Count,
            });
        }
    }

    private Dictionary<long, CompareSource> LoadCompareSources() => _db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT id, file_path, source, file_hash FROM images;";
        var map = new Dictionary<long, CompareSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt64(0)] = new CompareSource(
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                !reader.IsDBNull(2) && reader.GetString(2) == "remote",
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
        }
        return map;
    });

    /// <summary>
    /// 终审输入优先用已缓存 256px Thumb（远程图不必下载整图）；
    /// 本地行无缩略图时退回原图路径；远程行无缓存返回 null（跳过该对）。
    /// </summary>
    private string? ResolveComparePath(
        Dictionary<long, CompareSource> images, long imageId)
    {
        if (!images.TryGetValue(imageId, out CompareSource source))
            return null;

        string? thumbnail = FindThumbnailPath(source.Hash);
        if (thumbnail is not null)
            return thumbnail;

        if (source.Remote || string.IsNullOrWhiteSpace(source.Path))
            return null;
        return source.Path;
    }

    // ── 聚类与落表 ────────────────────────────────────────────────────

    private static List<CandidateGroup> BuildGroups(
        Dictionary<(long A, long B), CandidatePair> pairs)
    {
        var union = new UnionFind();
        var bestByImage = new Dictionary<long, CandidateMember>();

        foreach (KeyValuePair<(long A, long B), CandidatePair> entry in pairs)
        {
            (long a, long b) = entry.Key;
            CandidatePair pair = entry.Value;
            union.Union(a, b);
            ConsiderMember(bestByImage, a, pair);
            ConsiderMember(bestByImage, b, pair);
        }

        var components = new Dictionary<long, List<long>>();
        foreach (long imageId in union.Ids)
        {
            long root = union.Find(imageId);
            if (!components.TryGetValue(root, out List<long>? members))
            {
                members = [];
                components[root] = members;
            }
            members.Add(imageId);
        }

        var groups = new List<CandidateGroup>();
        foreach (List<long> members in components.Values)
        {
            if (members.Count < 2)
                continue;

            members.Sort();
            var groupMembers = new List<CandidateMember>(members.Count);
            bool allExact = true;
            foreach (long imageId in members)
            {
                CandidateMember member = bestByImage[imageId];
                if (!member.Exact)
                    allExact = false;
                groupMembers.Add(member);
            }
            groups.Add(new CandidateGroup(groupMembers, allExact));
        }

        groups.Sort((x, y) => x.Members[0].ImageId.CompareTo(y.Members[0].ImageId));
        return groups;
    }

    /// <summary>同一图片命中多个候选对时，按层级优先级取最高（同级取分数）。</summary>
    private static void ConsiderMember(
        Dictionary<long, CandidateMember> best, long imageId, CandidatePair pair)
    {
        if (best.TryGetValue(imageId, out CandidateMember? existing) &&
            PairRank(pair.Level) < PairRank(existing.Level))
            return;
        if (existing is not null &&
            PairRank(pair.Level) == PairRank(existing.Level) &&
            pair.Score <= existing.Score)
            return;

        best[imageId] = new CandidateMember(imageId, pair.Exact, pair.Score, pair.Level);
    }

    private void Persist(List<CandidateGroup> groups)
    {
        string now = DbTime.Now();
        _db.RunInTransaction((conn, tx) =>
        {
            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                // 新一轮结果替换旧 pending；人工处理过的历史保留。
                clear.CommandText =
                    "DELETE FROM duplicate_candidates WHERE status = 'pending';";
                clear.ExecuteNonQuery();
            }

            foreach (CandidateGroup group in groups)
            {
                string groupId = Guid.NewGuid().ToString("N");
                foreach (CandidateMember member in group.Members)
                {
                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO duplicate_candidates
                            (group_id, image_id, kind, level, score, status, created_at, updated_at)
                        VALUES
                            ($group, $image, $kind, $level, $score, 'pending', $now, $now);
                        """;
                    insert.Parameters.AddWithValue("$group", groupId);
                    insert.Parameters.AddWithValue("$image", member.ImageId);
                    insert.Parameters.AddWithValue("$kind", member.Exact ? "exact" : "variant");
                    insert.Parameters.AddWithValue("$level", member.Level);
                    insert.Parameters.AddWithValue("$score", member.Score);
                    insert.Parameters.AddWithValue("$now", now);
                    insert.ExecuteNonQuery();
                }
            }
        });
    }

    private List<PhashRow> LoadPhashes() => _db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText =
            "SELECT id, phash FROM images WHERE phash IS NOT NULL AND phash != '' ORDER BY id;";
        var list = new List<PhashRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (PerceptualHashService.TryParseHex(reader.GetString(1)) is not ulong hash)
                continue;
            list.Add(new PhashRow(reader.GetInt64(0), hash));
        }
        return list;
    });

    private int CountImages() => _db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM images;";
        return Convert.ToInt32(command.ExecuteScalar());
    });

    // ── 内部数据 ──────────────────────────────────────────────────────

    private readonly record struct PhashRow(long ImageId, ulong Hash);

    private readonly record struct CompareSource(string Path, bool Remote, string Hash);

    private sealed record CandidatePair(bool Exact, double Score, string Level);

    private sealed record CandidateMember(long ImageId, bool Exact, double Score, string Level);

    private sealed record CandidateGroup(List<CandidateMember> Members, bool AllExact);

    private sealed record VectorEntry(long ImageId, float[] Vector, double Norm);

    private sealed class UnionFind
    {
        private readonly Dictionary<long, long> _parent = [];
        private readonly Dictionary<long, int> _size = [];

        public IEnumerable<long> Ids => _parent.Keys;

        public long Find(long id)
        {
            if (!_parent.TryGetValue(id, out long parent))
            {
                _parent[id] = id;
                _size[id] = 1;
                return id;
            }
            if (parent == id)
                return id;

            long root = Find(parent);
            _parent[id] = root;
            return root;
        }

        public void Union(long first, long second)
        {
            long rootA = Find(first);
            long rootB = Find(second);
            if (rootA == rootB)
                return;

            if (_size[rootA] < _size[rootB])
                (rootA, rootB) = (rootB, rootA);
            _parent[rootB] = rootA;
            _size[rootA] += _size[rootB];
        }
    }
}
