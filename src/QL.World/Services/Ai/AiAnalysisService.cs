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
    private readonly object _lock = new();
    private bool _isRunning;

    public AiAnalysisService(
        IAiService ai,
        DatabaseService db,
        TagService tags,
        NotificationService notifications,
        Action<string>? log = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _log = log ?? (_ => { });
    }

    public bool IsRunning
    {
        get { lock (_lock) return _isRunning; }
    }

    public int ProcessedCount { get; private set; }
    public int TotalCount { get; private set; }

    /// <summary>
    /// Runs AI analysis over images that have no embedding yet.
    /// Returns the count of successfully analyzed images.
    /// </summary>
    public async Task<int> RunBatchAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (!_ai.IsAvailable)
        {
            _notifications.Publish(
                NotificationKind.AiBatchCompleted,
                "AI 分析未运行",
                "AI 服务不可用，请在设置中启用并配置云端或本地模型。");
            return 0;
        }

        lock (_lock)
        {
            if (_isRunning) return 0;
            _isRunning = true;
        }

        ProcessedCount = 0;
        int successes = 0;
        try
        {
            // Loop until no more unanalyzed images or cancellation.
            while (!cancellationToken.IsCancellationRequested)
            {
                List<(long Id, string Path)> batch = GetPendingBatch(batchSize);
                if (batch.Count == 0) break;

                TotalCount = Math.Max(TotalCount, ProcessedCount + batch.Count);

                foreach (var (id, path) in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        AiAnalysisResult result = await _ai.AnalyzeImageAsync(path, cancellationToken);
                        if (result.IsError)
                        {
                            _log($"[AI] image {id} failed: {result.ErrorMessage}");
                            continue;
                        }

                        PersistResult(id, result);
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

                _notifications.Publish(
                    NotificationKind.AiBatchProgress,
                    "AI 分析进行中",
                    $"已处理 {ProcessedCount} 张，成功 {successes} 张。");
            }

            _notifications.Publish(
                NotificationKind.AiBatchCompleted,
                cancellationToken.IsCancellationRequested ? "AI 分析已取消" : "AI 分析完成",
                $"共处理 {ProcessedCount} 张，成功 {successes} 张。");
        }
        catch (OperationCanceledException)
        {
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

    private List<(long Id, string Path)> GetPendingBatch(int limit) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT i.id, i.file_path
                FROM images i
                WHERE i.is_missing = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM image_embeddings e WHERE e.image_id = i.id
                  )
                ORDER BY i.id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            var list = new List<(long, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetString(1)));
            return list;
        });

    private void PersistResult(long imageId, AiAnalysisResult result)
    {
        // Tags (manual rows are protected by TagService).
        if (result.Tags.Count > 0)
        {
            var tagList = result.Tags.Select(t => t.Tag).ToList();
            _tags.AddTags([imageId], tagList, TagSource.Ai);
            // TODO: confidence storage — image_tags.confidence column exists;
            // current AddTags applies a single confidence. Store per-tag in M3-C refinement.
        }

        // Primary category (only when NULL).
        if (!string.IsNullOrWhiteSpace(result.PrimaryCategory))
        {
            _db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE images
                    SET primary_category = $cat, updated_at = $now
                    WHERE id = $id AND primary_category IS NULL;
                    """;
                cmd.Parameters.AddWithValue("$cat", result.PrimaryCategory);
                cmd.Parameters.AddWithValue("$now", DbTime.Now());
                cmd.Parameters.AddWithValue("$id", imageId);
                cmd.ExecuteNonQuery();
            });
        }

        // Embedding (self-describing BLOB). Always write a row on success —
        // even when the backend produced no vector — so GetPendingBatch's
        // NOT EXISTS check marks the image as analyzed and avoids re-processing.
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO image_embeddings (image_id, model_id, dim, embedding, created_at)
                VALUES ($id, $model, $dim, $blob, $now);
                """;
            float[]? emb = result.Embedding;
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.Parameters.AddWithValue("$model", result.ModelId ?? "none");
            cmd.Parameters.AddWithValue("$dim", emb?.Length ?? 0);
            cmd.Parameters.AddWithValue("$blob", emb is { Length: > 0 } ? FloatsToBytes(emb) : []);
            cmd.Parameters.AddWithValue("$now", DbTime.Now());
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
