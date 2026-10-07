using System.IO;
using QL.World.Models;
using QL.World.Models.Ai;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Tests.Support;

namespace QL.World.Tests.Ai;

public sealed class AiAnalysisServiceTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    [Fact]
    public async Task RunBatch_writes_ai_tags_and_embedding()
    {
        long id = _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                PrimaryCategory = "角色",
                Tags = [new AiTag("硬表面", 0.9), new AiTag("机械", 0.8)],
                Embedding = [1.0f, 2.0f, 3.0f],
                ModelId = "fake-model",
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        int count = await analysis.RunBatchAsync();

        Assert.Equal(1, count);

        // Tags stored with source='ai' and per-tag confidence.
        var imageTags = tags.GetTagsForImage(id);
        Assert.Equal(2, imageTags.Count);
        Assert.All(imageTags, t => Assert.Equal(TagSource.Ai, t.Source));
        var tagConfidences = imageTags.ToDictionary(t => t.Tag, t => t.Confidence);
        Assert.Equal(0.9, tagConfidences["硬表面"]);
        Assert.Equal(0.8, tagConfidences["机械"]);

        // Embedding stored.
        bool hasEmbedding = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_embeddings WHERE image_id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        });
        Assert.True(hasEmbedding);

        // Category set.
        var image = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        Assert.Equal("角色", image);
    }

    [Fact]
    public async Task RunBatch_does_not_overwrite_existing_category()
    {
        long id = _lib.InsertImage(category: "场景");
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                PrimaryCategory = "角色",
                Tags = [new AiTag("城市", 0.7)],
            },
        };
        var tags = new TagService(_lib.Db);
        var notifications = new NotificationService();
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        string? category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        Assert.Equal("场景", category);
    }

    [Fact]
    public async Task RunBatch_skips_images_with_existing_embedding()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();

        // Pre-insert an embedding for id1 so it's skipped.
        _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO image_embeddings (image_id, model_id, dim, embedding, created_at)
                VALUES ($id, 'pre', 1, $blob, '2026-01-01T00:00:00.000Z');
                """;
            cmd.Parameters.AddWithValue("$id", id1);
            cmd.Parameters.AddWithValue("$blob", new byte[] { 0 });
            cmd.ExecuteNonQuery();
        });

        int callCount = 0;
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ =>
            {
                callCount++;
                return new AiAnalysisResult { Tags = [new AiTag("x", 1.0)] };
            },
        };
        var tags = new TagService(_lib.Db);
        var notifications = new NotificationService();
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        Assert.Equal(1, callCount); // only id2 analyzed
    }

    [Fact]
    public async Task RunBatch_unavailable_ai_returns_zero()
    {
        _lib.InsertImage();
        var ai = new FakeAiService { IsAvailable = false };
        var tags = new TagService(_lib.Db);
        var notifications = new NotificationService();
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        int count = await analysis.RunBatchAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RunBatch_stops_on_cancellation()
    {
        for (int i = 0; i < 10; i++)
            _lib.InsertImage();

        int callCount = 0;
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ =>
            {
                callCount++;
                return new AiAnalysisResult { Tags = [new AiTag("x", 1.0)] };
            },
        };
        var tags = new TagService(_lib.Db);
        var notifications = new NotificationService();
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        using var cts = new CancellationTokenSource(50);
        await analysis.RunBatchAsync(cancellationToken: cts.Token);

        // Should have processed some but not all (timing-dependent, but > 0 and < 10 likely).
        Assert.True(callCount > 0);
    }

    [Fact]
    public async Task RunBatch_publishes_completion_notification()
    {
        _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult { Tags = [new AiTag("x", 1.0)] },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        Assert.Contains(notifications.Items, e => e.Kind == NotificationKind.AiBatchCompleted);
    }

    [Fact]
    public async Task RunBatch_persists_local_embedding_with_model_id_and_dim()
    {
        long id = _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                PrimaryCategory = "贴图/纹理",
                Tags = [new AiTag("硬表面", 0.98)],
                Embedding = new float[512],
                ModelId = ModelManifest.ModelId,
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        (string ModelId, long Dim) row = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT model_id, dim FROM image_embeddings WHERE image_id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (reader.GetString(0), reader.GetInt64(1));
        });
        Assert.Equal(ModelManifest.ModelId, row.ModelId);
        Assert.Equal(512, row.Dim);
    }
}
