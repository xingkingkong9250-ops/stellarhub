using System.IO;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Models.Ai;

namespace QL.World.Services.Ai;

/// <summary>
/// AI 自适应学习服务（v0.10.0）：不重训模型的应用层「越用越准」管线，
/// 同时为将来有硬件支持时的模型微调留存训练数据。
///
/// 三套方案（均作用于本地后端，云端建议由云端模型知识决定）：
/// 1. 候选词表学习——用户人工标签进入候选词表，经 text_model 计算文本
///    embedding 与图像 embedding 做余弦相似度打分（缓存于
///    learned_tag_embeddings），AI 之后能打出用户领域的自定义词；
/// 2. 修正偏好重排——统计每个 AI 标签被用户保留/删除的比例（保留率），
///    低保留率标签剔除、其余按保留率降权重排；
/// 3. 参考样本 kNN——用户修正过的图（embedding + 最终人工标签）作为
///    经验库，新图分析时取 k 近邻按相似度加权投票，与零样本排序融合。
///
/// 训练数据留存：ai_tag_suggestions（AI 原始建议快照，永不删除）+
/// tag_feedback（用户编辑事件日志，永不删除）。二者与 image_tags 最终态、
/// image_embeddings 对齐即得完整的 (image, ai_tags, human_tags, 偏好) 数据集。
/// </summary>
public sealed class AiLearningService
{
    /// <summary>精排后最多保留的标签数。</summary>
    public const int MaxRefineTags = 12;
    private const int ReferenceK = 8;
    private const int MaxReferenceSamples = 4000;
    private const int MaxLearnedTags = 32;
    /// <summary>学习词表标签入选的最低余弦相似度。</summary>
    private const double LearnedTagThreshold = 0.20;
    /// <summary>kNN 投票标签的最低置信度折算。</summary>
    private const double KnnVoteThreshold = 0.25;
    /// <summary>保留率低于该值的 AI 标签从候选中剔除（需至少 MinRetentionEvents 条建议）。</summary>
    private const double RetentionDropBelow = 0.30;
    private const int RetentionMinEvents = 3;
    /// <summary>结果置信度下限。</summary>
    private const double MinScore = 0.15;

    private readonly DatabaseService _db;

    public AiLearningService(DatabaseService db) =>
        _db = db ?? throw new ArgumentNullException(nameof(db));

    // ───────────────────────── 训练数据留存 ─────────────────────────

    /// <summary>
    /// 记录一次分析的 AI 原始建议快照（含主分类）。建议永远追加，
    /// 用户后续删除标签不回写此表——训练时与最终态对比即得正负样本。
    /// </summary>
    public void RecordSuggestions(long imageId, AiAnalysisResult result, string backend)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsError) return;
        string now = DbTime.Now();

        _db.RunInTransaction((conn, tx) =>
        {
            foreach (AiTag tag in result.Tags)
                InsertSuggestion(conn, tx, imageId, tag.Tag, tag.Confidence, 0, backend, result.ModelId, now);
            if (!string.IsNullOrWhiteSpace(result.PrimaryCategory))
                InsertSuggestion(conn, tx, imageId, result.PrimaryCategory!, null, 1, backend, result.ModelId, now);
        });
    }

    private static void InsertSuggestion(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        long imageId, string tag, double? confidence, int isCategory,
        string backend, string? modelId, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ai_tag_suggestions (image_id, tag, confidence, is_primary_category, backend, model_id, created_at)
            VALUES ($id, $tag, $conf, $cat, $backend, $model, $now);
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$tag", tag);
        cmd.Parameters.AddNullable("$conf", confidence);
        cmd.Parameters.AddWithValue("$cat", isCategory);
        cmd.Parameters.AddWithValue("$backend", backend);
        cmd.Parameters.AddNullable("$model", modelId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 记录用户标签编辑事件（TagService.TagAdded/TagRemoved 订阅转发）。
    /// 只追加永不删除——偏好统计与困难负样本的原始依据。
    /// </summary>
    public void RecordFeedback(long imageId, string tag, string action, TagSource source) =>
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tag_feedback (image_id, tag, action, tag_source, created_at)
                VALUES ($id, $tag, $action, $source, $now);
                """;
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.Parameters.AddWithValue("$tag", tag);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$source", source == TagSource.Manual ? "manual" : "ai");
            cmd.Parameters.AddWithValue("$now", DbTime.Now());
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// 方案二：各 AI 标签的保留率 = 1 − 被删次数 / 建议次数（0..1）。
    /// 仅统计有建议记录的标签；用户从未删过的标签不在返回值中（视为 1.0）。
    /// </summary>
    public Dictionary<string, double> GetRetentionRates()
    {
        return _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.tag,
                       COUNT(*) AS suggested,
                       (SELECT COUNT(*) FROM tag_feedback f
                        WHERE f.tag = s.tag AND f.action IN ('removed', 'bulk_removed')) AS removed
                FROM ai_tag_suggestions s
                GROUP BY s.tag;
                """;
            var rates = new Dictionary<string, double>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string tag = reader.GetString(0);
                long suggested = reader.GetInt64(1);
                long removed = reader.GetInt64(2);
                if (suggested > 0)
                    rates[tag] = Math.Clamp(1.0 - (double)removed / suggested, 0.0, 1.0);
            }
            return rates;
        });
    }

    // ───────────────────────── 方案一：词表学习 ─────────────────────────

    /// <summary>
    /// 学习词表：用户人工标签按使用频次倒序（排除保留率过低的词）。
    /// 这些词经 text_model 计算文本 embedding 后即可被零样本打分。
    /// </summary>
    public List<string> GetLearnedTags(int maxTags = MaxLearnedTags)
    {
        Dictionary<string, double> retention = GetRetentionRates();
        return _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT tag, COUNT(*) AS uses
                FROM image_tags
                WHERE source = 'manual'
                GROUP BY tag
                ORDER BY uses DESC, tag
                LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$max", maxTags + retention.Count);
            var tags = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string tag = reader.GetString(0);
                if (retention.TryGetValue(tag, out double rate) &&
                    rate < RetentionDropBelow)
                    continue; // 用户基本不保留的词不进词表
                tags.Add(tag);
                if (tags.Count >= maxTags) break;
            }
            return tags;
        });
    }

    /// <summary>
    /// 取学习词表的文本 embedding（learned_tag_embeddings 缓存，未命中时经
    /// backend.EmbedTextAsync 计算并回填）。后端无法产出文本 embedding 时
    /// 返回空字典（云端 / Disabled）。
    /// </summary>
    public async Task<Dictionary<string, float[]>> GetLearnedEmbeddingsAsync(
        IReadOnlyList<string> tags,
        IAiService backend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(backend);
        var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (tags.Count == 0) return result;

        Dictionary<string, float[]> cached = _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT tag, dim, embedding FROM learned_tag_embeddings WHERE dim > 0;";
            var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string tag = reader.GetString(0);
                long dim = reader.GetInt64(1);
                byte[] blob = (byte[])reader.GetValue(2);
                if (dim > 0 && blob.Length == dim * sizeof(float))
                    map[tag] = BytesToFloats(blob);
            }
            return map;
        });

        List<string> missing = tags.Where(t => !cached.ContainsKey(t)).ToList();
        foreach (string tag in missing)
        {
            float[]? embedding = await backend.EmbedTextAsync(tag, cancellationToken);
            if (embedding is not { Length: > 0 }) continue;
            cached[tag] = embedding;
            PersistLearnedEmbedding(tag, embedding, backend);
        }

        foreach (string tag in tags)
            if (cached.TryGetValue(tag, out float[]? emb))
                result[tag] = emb;
        return result;
    }

    private void PersistLearnedEmbedding(string tag, float[] embedding, IAiService backend)
    {
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO learned_tag_embeddings (tag, dim, embedding, model_id, updated_at)
                VALUES ($tag, $dim, $blob, $model, $now);
                """;
            cmd.Parameters.AddWithValue("$tag", tag);
            cmd.Parameters.AddWithValue("$dim", embedding.Length);
            cmd.Parameters.AddWithValue("$blob", FloatsToBytes(embedding));
            cmd.Parameters.AddWithValue("$model", backend.ModelId());
            cmd.Parameters.AddWithValue("$now", DbTime.Now());
            cmd.ExecuteNonQuery();
        });
    }

    // ───────────────────────── 方案三：参考样本 kNN ─────────────────────────

    /// <summary>参考样本：图片 embedding + 该图的人工标签（用户确认过的最终态）。</summary>
    public sealed record ReferenceSample(long ImageId, float[] Embedding, IReadOnlyList<string> Tags);

    /// <summary>
    /// 经验库：有 embedding 且有 manual 标签的图（仅本地来源 embedding，
    /// 保证与本地分析出的图像 embedding 同一向量空间）。
    /// </summary>
    public List<ReferenceSample> GetReferenceSamples(int max = MaxReferenceSamples)
    {
        return _db.Run(conn =>
        {
            var tagsByImage = new Dictionary<long, List<string>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT image_id, tag FROM image_tags WHERE source = 'manual' ORDER BY image_id;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    if (!tagsByImage.TryGetValue(id, out List<string>? list))
                        tagsByImage[id] = list = [];
                    list.Add(reader.GetString(1));
                }
            }
            if (tagsByImage.Count == 0) return [];

            var samples = new List<ReferenceSample>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT image_id, dim, embedding
                    FROM image_embeddings
                    WHERE dim > 0 AND source = 'local'
                    ORDER BY image_id
                    LIMIT $max;
                    """;
                cmd.Parameters.AddWithValue("$max", max);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    long dim = reader.GetInt64(1);
                    byte[] blob = (byte[])reader.GetValue(2);
                    if (dim <= 0 || blob.Length != dim * sizeof(float)) continue;
                    if (!tagsByImage.TryGetValue(id, out List<string>? tags)) continue;
                    samples.Add(new ReferenceSample(id, BytesToFloats(blob), tags));
                }
            }
            return samples;
        });
    }

    // ───────────────────────── 融合入口 ─────────────────────────

    /// <summary>
    /// 对一次成功的本地分析结果做自适应精排：零样本分（原始）⊕ 学习词表
    /// 零样本分 ⊕ kNN 投票，再按保留率降权/剔除。任何一步数据不足都
    /// 自动退化为原始结果；本方法永不抛出。
    /// </summary>
    public async Task<AiAnalysisResult> RefineAsync(
        AiAnalysisResult result,
        IAiService backend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(backend);
        try
        {
            if (result.IsError || result.Embedding is not { Length: > 0 })
                return result;

            // 原始零样本分打底。
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (AiTag tag in result.Tags)
                scores[tag.Tag] = Math.Max(scores.GetValueOrDefault(tag.Tag), tag.Confidence);

            // 方案一：学习词表零样本打分（用户词进入候选）。
            List<string> learned = GetLearnedTags();
            Dictionary<string, float[]> learnedEmbeddings =
                await GetLearnedEmbeddingsAsync(learned, backend, cancellationToken);
            foreach ((string tag, float[] emb) in learnedEmbeddings)
            {
                double cos = Cosine(result.Embedding, emb);
                if (cos >= LearnedTagThreshold)
                    scores[tag] = Math.Max(scores.GetValueOrDefault(tag), cos);
            }

            // 方案三：kNN 相似度加权投票（参考样本的人工标签）。
            List<ReferenceSample> refs = GetReferenceSamples();
            if (refs.Count > 0)
            {
                IEnumerable<(ReferenceSample Sample, double Sim)> top = refs
                    .Select(r => (r, Sim: Cosine(result.Embedding, r.Embedding)))
                    .Where(x => x.Sim > 0)
                    .OrderByDescending(x => x.Sim)
                    .Take(ReferenceK);
                var votes = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach ((ReferenceSample sample, double sim) in top)
                    foreach (string tag in sample.Tags)
                        votes[tag] = votes.GetValueOrDefault(tag) + sim;
                double maxVote = votes.Count > 0 ? votes.Values.Max() : 0;
                if (maxVote > 0)
                    foreach ((string tag, double vote) in votes)
                    {
                        double confidence = KnnVoteThreshold + vote / maxVote * 0.6;
                        scores[tag] = Math.Max(scores.GetValueOrDefault(tag), confidence);
                    }
            }

            // 方案二：保留率降权 / 剔除（用户总删的词不再出现）。
            Dictionary<string, double> retention = GetRetentionRates();
            foreach (string tag in scores.Keys.ToList())
            {
                if (!retention.TryGetValue(tag, out double rate)) continue;
                if (rate < RetentionDropBelow)
                    scores[tag] = -1; // 剔除
                else
                    scores[tag] *= Math.Clamp(0.5 + rate * 0.5, 0.5, 1.0); // 最高降到 0.75×
            }

            List<AiTag> refined = scores
                .Where(kv => kv.Value >= MinScore)
                .OrderByDescending(kv => kv.Value)
                .Take(MaxRefineTags)
                .Select(kv => new AiTag(kv.Key, Math.Clamp(kv.Value, 0.01, 0.99)))
                .ToList();

            if (refined.Count == 0)
                return result;
            // M12 (v0.13.0): 重排后标签变了，重新推导分类。
            string? derivedCategory = TaxonomyService.DeriveCategory(refined)
                ?? result.PrimaryCategory;
            return new AiAnalysisResult
            {
                PrimaryCategory = derivedCategory,
                Tags = refined,
                Embedding = result.Embedding,
                ModelId = result.ModelId,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 学习管线故障绝不影响主分析——退回原始结果。
            Console.Error.WriteLine($"[AI-LEARN] refine failed: {ex.Message}");
            return result;
        }
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na > 0 && nb > 0 ? dot / (Math.Sqrt(na) * Math.Sqrt(nb)) : 0;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}

/// <summary>IAiService 的模型标识便捷读取（ModelId 缺省时回退 Mode）。</summary>
internal static class AiLearningBackendExtensions
{
    public static string ModelId(this IAiService backend) => backend.Mode;
}
