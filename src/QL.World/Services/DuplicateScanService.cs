using System.IO;
using QL.World.Data;

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

    /// <summary>用户取消：候选表未写入。</summary>
    public bool Cancelled { get; set; }
}

/// <summary>
/// 0.16.0 DUP-C: 全库近重复扫描。
/// L1 = 64 位 dHash + 内存 BK-tree（汉明距离 ≤10，严格同图）；
/// L2 = image_embeddings 同 model_id 分块余弦（≥0.92，近似变体）；
/// 候选对取传递闭包成组，每组成员各写一行 duplicate_candidates（pending）。
/// 新一轮扫描先清 pending 旧行，resolved/dismissed 历史保留；取消时不写候选表。
/// 存量 phash 缺失时按需从 &lt;缩略图根&gt;\&lt;hash2&gt;\&lt;hash&gt;_thumb.webp 补算。
/// </summary>
public sealed class DuplicateScanService
{
    /// <summary>L2 近似变体阈值：同 model_id 内余弦 ≥0.92 视为同一画面变体。</summary>
    public const double VariantMinCosine = 0.92;

    /// <summary>L2 分块大小：块内与跨块成对比较，控制单次工作集。</summary>
    private const int VECTOR_CHUNK_SIZE = 256;

    /// <summary>L1 配对进度上报间隔（张）。</summary>
    private const int L1_PROGRESS_INTERVAL = 64;

    private readonly DatabaseService _db;
    private readonly PerceptualHashService _hasher;
    private readonly string? _thumbnailRoot;

    public DuplicateScanService(
        DatabaseService db,
        PerceptualHashService? hasher = null,
        string? thumbnailRoot = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hasher = hasher ?? new PerceptualHashService();
        _thumbnailRoot = string.IsNullOrWhiteSpace(thumbnailRoot) ? null : thumbnailRoot;
    }

    /// <summary>
    /// 后台执行一次全库扫描。令牌只被扫描核心逐阶段检查（不传给
    /// Task.Run，避免预取消时委托完全不执行而拿不到 Cancelled 结果）。
    /// </summary>
    public Task<DuplicateScanResult> ScanAsync(
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanCore(progress, cancellationToken));

    private DuplicateScanResult ScanCore(
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
        CollectL1Pairs(phashes, tree, pairs, progress, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            result.Cancelled = true;
            return result;
        }

        result.VectorCount = CollectL2Pairs(pairs, progress, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            result.Cancelled = true;
            return result;
        }

        List<CandidateGroup> groups = BuildGroups(pairs);
        result.GroupCount = groups.Count;
        result.ExactGroupCount = groups.Count(group => group.AllExact);
        result.VariantGroupCount = result.GroupCount - result.ExactGroupCount;
        result.CandidateCount = groups.Sum(group => group.Members.Count);

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
        if (_thumbnailRoot is null)
            return null;

        string key = fileHash.ToLowerInvariant();
        if (key.Length < 2)
            return null;

        string path = Path.Combine(_thumbnailRoot, key[..2], $"{key}_thumb.webp");
        if (!File.Exists(path))
            return null;

        return _hasher.TryCompute(path) is ulong hash
            ? PerceptualHashService.ToHex(hash)
            : null;
    }

    // ── L1：BK-tree 半径配对 ─────────────────────────────────────────

    private static void CollectL1Pairs(
        List<PhashRow> phashes,
        PhashBkTree tree,
        Dictionary<(long A, long B), CandidatePair> pairs,
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
            tree.Search(row.Hash, PerceptualHashService.SameImageMaxDistance, row.ImageId, hits);
            foreach ((long otherId, int distance) in hits)
            {
                if (otherId == row.ImageId)
                    continue;
                // 距离取倒数转为相似度；同图距离越小得分越高。
                double score = 1.0 / (1.0 + distance);
                AddPair(pairs, row.ImageId, otherId, exact: true, score);
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

                    CompareBlocks(blocks[a], blocks[b], sameBlock: a == b, pairs);
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
        Dictionary<(long A, long B), CandidatePair> pairs)
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
                if (similarity >= VariantMinCosine)
                    AddPair(pairs, a.ImageId, b.ImageId, exact: false, Math.Min(1.0, similarity));
            }
        }
    }

    private static void AddPair(
        Dictionary<(long A, long B), CandidatePair> pairs,
        long first,
        long second,
        bool exact,
        double score)
    {
        (long, long) key = first < second ? (first, second) : (second, first);
        if (pairs.TryGetValue(key, out CandidatePair? existing))
        {
            if (exact)
            {
                // L1 覆盖 L2；同一对多条 L1 边时保留最高相似度（最短距离）。
                if (!existing.Exact || score > existing.Score)
                    pairs[key] = new CandidatePair(true, score);
            }
            else if (!existing.Exact && score > existing.Score)
            {
                pairs[key] = new CandidatePair(false, score);
            }
            return;
        }
        pairs[key] = new CandidatePair(exact, score);
    }

    // ── 聚类与落表 ────────────────────────────────────────────────────

    private static List<CandidateGroup> BuildGroups(
        Dictionary<(long A, long B), CandidatePair> pairs)
    {
        var union = new UnionFind();
        var bestExact = new Dictionary<long, double>();
        var bestVariant = new Dictionary<long, double>();

        foreach (KeyValuePair<(long A, long B), CandidatePair> entry in pairs)
        {
            (long a, long b) = entry.Key;
            CandidatePair pair = entry.Value;
            union.Union(a, b);
            Dictionary<long, double> best = pair.Exact ? bestExact : bestVariant;
            if (!best.TryGetValue(a, out double scoreA) || pair.Score > scoreA)
                best[a] = pair.Score;
            if (!best.TryGetValue(b, out double scoreB) || pair.Score > scoreB)
                best[b] = pair.Score;
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
                bool exact = bestExact.TryGetValue(imageId, out double exactScore);
                if (!exact)
                    allExact = false;
                double score = exact ? exactScore : bestVariant.GetValueOrDefault(imageId);
                groupMembers.Add(new CandidateMember(imageId, exact, score));
            }
            groups.Add(new CandidateGroup(groupMembers, allExact));
        }

        groups.Sort((x, y) => x.Members[0].ImageId.CompareTo(y.Members[0].ImageId));
        return groups;
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
                    insert.Parameters.AddWithValue("$level", member.Exact ? "L1" : "L2");
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

    private sealed record CandidatePair(bool Exact, double Score);

    private sealed record CandidateMember(long ImageId, bool Exact, double Score);

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
