using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Models.Ai;
using QL.World.Services.Ai;

namespace QL.World.Services;

/// <summary>
/// Orchestrates batch AI analysis over the library. Runs asynchronously,
/// supports cancellation, reports progress through NotificationService,
/// and persists AI tags (source='ai') + embeddings. Manual tags are never
/// overwritten — TagService enforces that contract.
/// </summary>
public sealed class AiAnalysisService
{
    private readonly IAiService _ai;
    private readonly DatabaseService _db;
    private readonly TagService _tags;
    private readonly NotificationService _notifications;
    private readonly Action<string> _log;
    private readonly Func<int, IAiService>? _backendResolver;
    private readonly AiLearningService? _learning;
    private readonly Func<bool>? _learningEnabled;
    private readonly object _lock = new();
    private bool _isRunning;

    public AiAnalysisService(
        IAiService ai,
        DatabaseService db,
        TagService tags,
        NotificationService notifications,
        Action<string>? log = null,
        Func<int, IAiService>? backendResolver = null,
        AiLearningService? learning = null,
        Func<bool>? learningEnabled = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _log = log ?? (_ => { });
        _backendResolver = backendResolver;
        _learning = learning;
        _learningEnabled = learningEnabled;
    }

    public bool IsRunning
    {
        get { lock (_lock) return _isRunning; }
    }

    /// <summary>v0.8.1: 进度通知最小发布间隔，防长任务刷屏消息中心。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    /// <summary>v0.9.0: 每批处理完触发（已处理数, 总数），Shell 底部信息提示栏订阅显示进度。</summary>
    public event Action<int, int>? BatchProgress;

    public int ProcessedCount { get; private set; }
    public int TotalCount { get; private set; }

    /// <summary>
    /// Runs AI analysis over pending images. v0.9.0 规则调整：
    /// 待分析 = <b>无任何标签（人工或 AI 写入均算）且无分类</b>的图；
    /// 有标签/分类的一律跳过，避免反复刷新已有标签分类。
    /// </summary>
    /// <param name="batchSize">每批取样上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="onlyImageIds">
    /// 作用域限定（W2 导入后自动分析：仅本次新增图）。null = 全库按规则取。
    /// </param>
    /// <param name="backendIdOverride">
    /// v0.12.0: 按次指定后端 id（W2 导入对话框选择），经构造时的
    /// backendResolver 解析；null = 使用默认后端（跟随 W3/A3 活跃后端）。
    /// </param>
    public async Task<int> RunBatchAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default,
        IReadOnlyList<long>? onlyImageIds = null,
        int? backendIdOverride = null)
    {
        IAiService backend = backendIdOverride is { } bid
            ? (_backendResolver?.Invoke(bid) ?? _ai)
            : _ai;
        if (!backend.IsAvailable)
        {
            _notifications.Publish(
                NotificationKind.AiBatchCompleted,
                "AI 分析未运行",
                backendIdOverride is null
                    ? "AI 服务不可用，请在设置中启用并配置云端或本地模型。"
                    : "所选 AI 后端不可用：请先在 设置 → AI 分析 中完成对应配置。");
            return 0;
        }

        lock (_lock)
        {
            if (_isRunning) return 0;
            _isRunning = true;
        }

        ProcessedCount = 0;
        int successes = 0;
        // v0.8.1: keyset 分页游标——失败图不写 embedding，若按"每次取最前面的
        // pending"循环会永远取到同一批永久失败图（实测 13 张损坏图触发
        // 十万级重复处理 + 消息刷屏死循环）。游标只前进，每张图每轮至多处理一次。
        long afterId = 0;
        DateTime lastProgressUtc = DateTime.MinValue;
        try
        {
            // Loop until no more unanalyzed images or cancellation.
            while (!cancellationToken.IsCancellationRequested)
            {
                List<(long Id, string Path)> batch =
                    GetPendingBatch(batchSize, afterId, onlyImageIds);
                if (batch.Count == 0) break;
                afterId = batch[^1].Id;

                TotalCount = Math.Max(TotalCount, ProcessedCount + batch.Count);

                foreach (var (id, path) in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        AiAnalysisResult result = await backend.AnalyzeImageAsync(path, cancellationToken);
                        if (result.IsError)
                        {
                            _log($"[AI] image {id} failed: {result.ErrorMessage}");
                            continue;
                        }

                        // v0.10.0: 自适应学习精排（词表学习 + kNN 投票 +
                        // 保留率重排），仅本地后端且 W3/A3 开关开启；
                        // 失败自动退回原始结果。训练数据快照不受开关影响。
                        if (_learning is { } learning &&
                            backend.Mode == "Local" &&
                            _learningEnabled?.Invoke() != false)
                            result = await learning.RefineAsync(result, backend, cancellationToken);

                        PersistResult(id, result, EmbeddingSource(backend));
                        successes++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log($"[AI] image {id} exception: {ex.Message}");
                    }
                    finally
                    {
                        ProcessedCount++;
                    }
                }

                // v0.9.0: 每批结束回调进度（Shell 状态栏显示），先于节流通知。
                BatchProgress?.Invoke(ProcessedCount, TotalCount);

                // v0.8.1: 进度通知节流（≥5s 一条）且只保留最新一条（发布前
                // ClearByKind 收敛），防止长任务把消息中心/未读徽标刷爆；
                // 最终结果由 AiBatchCompleted 单条记录。
                DateTime nowUtc = DateTime.UtcNow;
                if (nowUtc - lastProgressUtc >= ProgressInterval)
                {
                    lastProgressUtc = nowUtc;
                    _notifications.ClearByKind(NotificationKind.AiBatchProgress);
                    _notifications.Publish(
                        NotificationKind.AiBatchProgress,
                        "AI 分析进行中",
                        $"已处理 {ProcessedCount} 张，成功 {successes} 张。");
                }
            }

            _notifications.ClearByKind(NotificationKind.AiBatchProgress);
            int failed = ProcessedCount - successes;
            _notifications.Publish(
                NotificationKind.AiBatchCompleted,
                cancellationToken.IsCancellationRequested ? "AI 分析已取消" : "AI 分析完成",
                failed > 0
                    ? $"共处理 {ProcessedCount} 张，成功 {successes} 张，失败 {failed} 张。"
                    : $"共处理 {ProcessedCount} 张，成功 {successes} 张。");
        }
        catch (OperationCanceledException)
        {
            _notifications.ClearByKind(NotificationKind.AiBatchProgress);
            _notifications.Publish(
                NotificationKind.AiBatchCompleted,
                "AI 分析已取消",
                $"已处理 {ProcessedCount} 张，成功 {successes} 张。");
        }
        finally
        {
            lock (_lock) _isRunning = false;
        }

        return successes;
    }

    private List<(long Id, string Path)> GetPendingBatch(
        int limit,
        long afterId,
        IReadOnlyList<long>? onlyImageIds) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            // v0.9.0: 待分析规则 = 无任何标签（人工或 AI 写入均算）且无分类；
            // 有标签/分类的图一律跳过（不再以 embedding 为准），避免反复
            // 刷新已有标签分类。onlyImageIds 非空时限定范围（W2 导入后
            // 仅分析本次新增），经 json_each 传入避免超长 IN 参数列表。
            command.CommandText = """
                SELECT i.id, i.file_path
                FROM images i
                WHERE i.is_missing = 0
                  AND i.id > $afterId
                  AND NOT EXISTS (
                      SELECT 1 FROM image_tags t WHERE t.image_id = i.id
                  )
                  AND (i.primary_category IS NULL OR i.primary_category = '')
                  AND ($scopeAll = 1 OR i.id IN (SELECT value FROM json_each($scopeIds)))
                ORDER BY i.id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$afterId", afterId);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$scopeAll", onlyImageIds is null ? 1 : 0);
            command.Parameters.AddWithValue(
                "$scopeIds",
                onlyImageIds is null ? "[]" : System.Text.Json.JsonSerializer.Serialize(onlyImageIds));

            var list = new List<(long, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetString(1)));
            return list;
        });

    private void PersistResult(long imageId, AiAnalysisResult result, string embeddingSource)
    {
        // v0.10.0: AI 原始建议快照（训练数据留存，永不删除）——用户后续
        // 删改标签不影响此表，将来微调时与最终态对齐即得正负样本。
        _learning?.RecordSuggestions(imageId, result, embeddingSource);

        // Tags (manual rows are protected by TagService).
        if (result.Tags.Count > 0)
        {
            var tagInputs = result.Tags
                .Select(tag => (tag.Tag, (double?)tag.Confidence))
                .ToList();
            _tags.AddTags([imageId], tagInputs, TagSource.Ai);
        }

        // M12 (v0.13.0): 标签驱动分类——由 TaxonomyService 从标签推导
        // primary_category，推导失败才回退 AI 原始 PrimaryCategory。
        string? derivedCategory = TaxonomyService.DeriveCategory(result.Tags);
        string? finalCategory = derivedCategory ?? result.PrimaryCategory;
        if (!string.IsNullOrWhiteSpace(finalCategory))
        {
            _db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE images
                    SET primary_category = $cat, updated_at = $now
                    WHERE id = $id AND primary_category IS NULL;
                    """;
                cmd.Parameters.AddWithValue("$cat", finalCategory);
                cmd.Parameters.AddWithValue("$now", DbTime.Now());
                cmd.Parameters.AddWithValue("$id", imageId);
                cmd.ExecuteNonQuery();
            });
        }

        PersistEmbedding(imageId, result, embeddingSource);

        // Notify SemanticSearchService so its in-memory cache is invalidated.
        EmbeddingsUpdated?.Invoke();
    }

    /// <summary>Raised after PersistResult writes an embedding row. SemanticSearchService subscribes to invalidate its cache.</summary>
    public event Action? EmbeddingsUpdated;

    /// <summary>
    /// U2-D (§4.C.2): map IAiService.Mode to the image_embeddings.source value
    /// ("local" / "cloud"). "Disabled" never reaches here (IsAvailable=false);
    /// any other value falls back to "local". v0.9.0: 跟随实际使用的 backend
    /// （W2/AI 按次覆盖云端/本地时 source 必须如实记录）。
    /// </summary>
    private string EmbeddingSource(IAiService backend) =>
        backend.Mode == "Cloud" ? "cloud" : "local";

    private void PersistEmbedding(long imageId, AiAnalysisResult result, string embeddingSource)
    {
        // Embedding (self-describing BLOB). Always write a row on success —
        // even when the backend produced no vector — so semantic search has
        // a row per analyzed image. v0.9.0 起"已分析"判定改为标签/分类驱动
        //（见 GetPendingBatch），embedding 仅服务语义搜索。
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO image_embeddings (image_id, model_id, dim, embedding, created_at, source)
                VALUES ($id, $model, $dim, $blob, $now, $source);
                """;
            float[]? emb = result.Embedding;
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.Parameters.AddWithValue("$model", result.ModelId ?? "none");
            cmd.Parameters.AddWithValue("$dim", emb?.Length ?? 0);
            cmd.Parameters.AddWithValue("$blob", emb is { Length: > 0 } ? FloatsToBytes(emb) : []);
            cmd.Parameters.AddWithValue("$now", DbTime.Now());
            cmd.Parameters.AddWithValue("$source", embeddingSource);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Converts a float array to a little-endian byte blob.</summary>
    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
