using QL.World.Models;
using QL.World.Models.Ai;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Tests.Support;

namespace QL.World.Tests.Ai;

/// <summary>
/// v0.10.0 自适应学习回归：训练数据留存（建议快照 + 编辑事件）、
/// 保留率统计、词表学习、参考样本 kNN 与 RefineAsync 融合。
/// </summary>
public sealed class AiLearningServiceTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly TagService _tags;
    private readonly AiLearningService _learning;

    public AiLearningServiceTests()
    {
        _tags = new TagService(_lib.Db);
        _learning = new AiLearningService(_lib.Db);
        // 与 App 组装一致：TagService 编辑事件 → tag_feedback 日志。
        _tags.TagAdded += (id, tag, source) =>
            _learning.RecordFeedback(id, tag, "added", source);
        _tags.TagRemoved += (id, tag, source) =>
            _learning.RecordFeedback(id, tag, "removed", source);
    }

    public void Dispose() => _lib.Dispose();

    private void InsertEmbedding(long imageId, float[] embedding, string source = "local")
    {
        byte[] blob = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, blob, 0, blob.Length);
        _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO image_embeddings (image_id, model_id, dim, embedding, created_at, source)
                VALUES ($id, 'test', $dim, $blob, '2026-01-01T00:00:00.000Z', $source);
                """;
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.Parameters.AddWithValue("$dim", embedding.Length);
            cmd.Parameters.AddWithValue("$blob", blob);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.ExecuteNonQuery();
        });
    }

    private static AiAnalysisResult Result(params AiTag[] tags) => new() { Tags = tags };

    private long SuggestionCount =>
        _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ai_tag_suggestions;";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        });

    private long FeedbackCount(string action, string source) =>
        _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tag_feedback WHERE action = $a AND tag_source = $s;";
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$s", source);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        });

    // ─── 训练数据留存 ─────────────────────────────────────────────────────

    [Fact]
    public void RecordSuggestions_writes_snapshot_including_category()
    {
        var result = new AiAnalysisResult
        {
            PrimaryCategory = "场景",
            Tags = [new AiTag("城市", 0.8), new AiTag("夜景", 0.6)],
            ModelId = "clip-vit-b32",
        };

        _learning.RecordSuggestions(42, result, "local");

        Assert.Equal(3, SuggestionCount); // 2 tags + 1 category
    }

    [Fact]
    public void RecordSuggestions_skips_error_results()
    {
        _learning.RecordSuggestions(1, new AiAnalysisResult { ErrorMessage = "bad" }, "local");
        Assert.Equal(0, SuggestionCount);
    }

    [Fact]
    public void RecordFeedback_persists_user_edit_events()
    {
        long id = _lib.InsertImage();
        _tags.AddTags([id], ["城市"], TagSource.Manual);

        _tags.RemoveTag(id, "城市");

        Assert.Equal(1, FeedbackCount("added", "manual"));
        Assert.Equal(1, FeedbackCount("removed", "manual"));
    }

    [Fact]
    public void DeleteTagEverywhere_logs_removal_per_image()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();
        _tags.AddTags([id1, id2], ["临时"], TagSource.Ai);

        _tags.DeleteTagEverywhere("临时");

        Assert.Equal(2, FeedbackCount("removed", "ai"));
    }

    // ─── 方案二：保留率 ───────────────────────────────────────────────────

    [Fact]
    public void GetRetentionRates_computes_keep_ratio()
    {
        // "城市" 建议 2 次未删 → 1.0；"废片" 建议 2 次删 1 次 → 0.5。
        var kept = Result(new AiTag("城市", 0.9));
        var junk = Result(new AiTag("废片", 0.9));
        _learning.RecordSuggestions(1, kept, "local");
        _learning.RecordSuggestions(2, kept, "local");
        _learning.RecordSuggestions(3, junk, "local");
        _learning.RecordSuggestions(4, junk, "local");
        _learning.RecordFeedback(3, "废片", "removed", TagSource.Ai);

        Dictionary<string, double> rates = _learning.GetRetentionRates();

        Assert.Equal(1.0, rates["城市"]);
        Assert.Equal(0.5, rates["废片"]);
    }

    // ─── 方案一：词表学习 ─────────────────────────────────────────────────

    [Fact]
    public void GetLearnedTags_orders_by_usage_and_drops_low_retention()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();
        long id3 = _lib.InsertImage();
        _tags.AddTags([id1, id2], ["城市"], TagSource.Manual);
        _tags.AddTags([id3], ["废词"], TagSource.Manual);
        // "废词" 曾被 AI 建议 3 次且全被删除 → 保留率 0，不进词表。
        var junk = Result(new AiTag("废词", 0.9));
        foreach (long id in new[] { id1, id2, id3 })
            _learning.RecordSuggestions(id, junk, "local");
        foreach (long id in new[] { id1, id2, id3 })
            _learning.RecordFeedback(id, "废词", "removed", TagSource.Ai);

        List<string> learned = _learning.GetLearnedTags();

        Assert.Equal(["城市"], learned);
    }

    // ─── 方案三：参考样本 ─────────────────────────────────────────────────

    [Fact]
    public void GetReferenceSamples_joins_manual_tags_with_local_embeddings()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();
        _tags.AddTags([id1], ["赛博", "夜景"], TagSource.Manual);
        _tags.AddTags([id2], ["只有AI"], TagSource.Ai);
        InsertEmbedding(id1, [1f, 0f, 0f]);
        InsertEmbedding(id2, [0f, 1f, 0f]);

        List<AiLearningService.ReferenceSample> samples = _learning.GetReferenceSamples();

        Assert.Single(samples); // id2 无人工标签 → 不入经验库
        Assert.Equal(id1, samples[0].ImageId);
        Assert.Equal(new[] { "赛博", "夜景" }.OrderBy(t => t, StringComparer.Ordinal), samples[0].Tags);
        Assert.Equal(3, samples[0].Embedding.Length);
    }

    // ─── 融合入口 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RefineAsync_fuses_learned_vocabulary_and_knn_votes()
    {
        // 参考样本：embedding [1,0,0] + 人工标签「赛博」。
        long refImage = _lib.InsertImage();
        _tags.AddTags([refImage], ["赛博"], TagSource.Manual);
        InsertEmbedding(refImage, [1f, 0f, 0f]);

        // 学习词表：「赛博」同时是人工标签 → 走文本 embedding 零样本打分。
        var backend = new FakeAiService
        {
            EmbedTextFunc = text => text == "赛博" ? [1f, 0f, 0f] : null,
        };

        // 待精排结果：图像 embedding 与参考样本同向 → kNN 命中「赛博」。
        var result = new AiAnalysisResult
        {
            Tags = [new AiTag("机器人", 0.5)],
            Embedding = [1f, 0f, 0f],
        };

        AiAnalysisResult refined = await _learning.RefineAsync(result, backend, CancellationToken.None);

        List<string> tags = refined.Tags.Select(t => t.Tag).ToList();
        Assert.Contains("机器人", tags);
        Assert.Contains("赛博", tags); // kNN 投票 + 词表学习双通道引入
    }

    [Fact]
    public async Task RefineAsync_drops_low_retention_tags()
    {
        var junk = Result(new AiTag("废词", 0.9));
        foreach (long id in new[] { 1L, 2L, 3L })
        {
            _learning.RecordSuggestions(id, junk, "local");
            _learning.RecordFeedback(id, "废词", "removed", TagSource.Ai);
        }

        var result = new AiAnalysisResult
        {
            Tags = [new AiTag("废词", 0.9), new AiTag("好词", 0.7)],
            Embedding = [1f, 0f, 0f],
        };

        AiAnalysisResult refined = await _learning.RefineAsync(
            result, new FakeAiService(), CancellationToken.None);

        Assert.DoesNotContain(refined.Tags, t => t.Tag == "废词");
        Assert.Contains(refined.Tags, t => t.Tag == "好词");
    }

    [Fact]
    public async Task RefineAsync_returns_original_when_no_embedding()
    {
        var result = Result(new AiTag("任意", 0.5));

        AiAnalysisResult refined = await _learning.RefineAsync(
            result, new FakeAiService(), CancellationToken.None);

        Assert.Same(result, refined);
    }
}
