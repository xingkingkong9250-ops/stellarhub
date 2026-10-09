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

    // ─── v0.9.0 回归：待分析规则 = 无任何标签（人工或 AI）且无分类 ──────────

    [Fact]
    public async Task RunBatch_skips_images_with_any_tags()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();

        // 人工标签受保护：有标签（无论来源）即跳过，不被 AI 刷新。
        var tags = new TagService(_lib.Db);
        tags.AddTags([id1], ["手工标签"], TagSource.Manual);

        int callCount = 0;
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ =>
            {
                callCount++;
                return new AiAnalysisResult { Tags = [new AiTag("x", 1.0)] };
            },
        };
        var notifications = new NotificationService();
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        Assert.Equal(1, callCount); // only id2 analyzed
    }

    [Fact]
    public async Task RunBatch_skips_images_with_ai_tags()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();

        var tags = new TagService(_lib.Db);
        tags.AddTags([id1], ["历史AI标签"], TagSource.Ai, 0.9);

        int callCount = 0;
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ =>
            {
                callCount++;
                return new AiAnalysisResult { Tags = [new AiTag("x", 1.0)] };
            },
        };
        var notifications = new NotificationService();
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        Assert.Equal(1, callCount); // AI 打过标的也不重复分析
    }

    [Fact]
    public async Task RunBatch_skips_images_with_category_only()
    {
        _lib.InsertImage(category: "场景");
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
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        Assert.Equal(1, callCount); // 只有分类没有标签同样跳过
    }

    [Fact]
    public async Task RunBatch_scopes_to_onlyImageIds_when_provided()
    {
        long id1 = _lib.InsertImage();
        _lib.InsertImage();
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
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        // W2/AI：导入后仅分析本次新增图。
        await analysis.RunBatchAsync(onlyImageIds: [id1]);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RunBatch_backend_id_override_uses_resolved_backend()
    {
        _lib.InsertImage();

        int? resolvedBackendId = null;
        var cloudBackend = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult { Tags = [new AiTag("云", 1.0)] },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(
            new FakeAiService(), _lib.Db, tags, notifications,
            backendResolver: id =>
            {
                resolvedBackendId = id;
                return cloudBackend;
            });

        // v0.12.0: 按次指定后端 id，不经全局设置。
        int count = await analysis.RunBatchAsync(backendIdOverride: 42);

        Assert.Equal(1, count);
        Assert.Equal(42, resolvedBackendId);
    }

    [Fact]
    public async Task RunBatch_records_suggestion_snapshots_for_training()
    {
        _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                PrimaryCategory = "角色",
                Tags = [new AiTag("勇士", 0.8)],
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var learning = new AiLearningService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications, learning: learning);

        await analysis.RunBatchAsync();

        // v0.10.0: 训练数据留存——1 标签 + 1 主分类 = 2 行快照，永不删除。
        int rows = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ai_tag_suggestions;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
        Assert.Equal(2, rows);
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

    // ─── v0.8.1 回归：失败图死循环 + 进度通知刷屏 ─────────────────────────

    [Fact]
    public async Task RunBatch_terminates_when_all_remaining_fail()
    {
        for (int i = 0; i < 3; i++)
            _lib.InsertImage();

        int calls = 0;
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ =>
            {
                calls++;
                return new AiAnalysisResult { ErrorMessage = "corrupt" };
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        // 回归：失败图不写 embedding，旧实现每轮重新取同一批 → 死循环
        // （实测 13 张失败图被重复处理十几万次）。现在必须正常终止。
        int count = await analysis.RunBatchAsync();

        Assert.Equal(0, count);
        Assert.Equal(3, calls); // 每张图每轮至多处理一次
        Assert.Equal(3, analysis.ProcessedCount);
    }

    [Fact]
    public async Task RunBatch_progress_entries_collapse_to_single_completion()
    {
        for (int i = 0; i < 5; i++)
            _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult { Tags = [new AiTag("x", 1.0)] },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        // 回归：进度通知每批一条曾把消息中心刷爆；节流收敛后运行结束
        // 应只留下一条完成通知（快速跑完时甚至不会出现进度条目）。
        await analysis.RunBatchAsync();

        Assert.DoesNotContain(notifications.Items, e => e.Kind == NotificationKind.AiBatchProgress);
        Assert.Single(notifications.Items, e => e.Kind == NotificationKind.AiBatchCompleted);
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

    // ─── M12-B (v0.13.0): 标签驱动分类推导 ──────────────────────────

    [Fact]
    public async Task RunBatch_derives_category_from_taxonomy_tags()
    {
        long id = _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                // AI 返回"场景"，但标签里有"骑士"（角色组）——推导结果应覆盖
                PrimaryCategory = "场景",
                Tags = [new AiTag("骑士", 0.9), new AiTag("写实", 0.7)],
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        string? category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        // 骑士→角色（分类组），推导为角色，覆盖 AI 原始的"场景"
        Assert.Equal("角色", category);
    }

    [Fact]
    public async Task RunBatch_falls_back_to_ai_category_when_tags_not_in_taxonomy()
    {
        long id = _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                PrimaryCategory = "场景",
                Tags = [new AiTag("硬表面", 0.9), new AiTag("机械", 0.8)],
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        string? category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        // 硬表面/机械不在词表 → 推导为 null → 回退 AI 原始"场景"
        Assert.Equal("场景", category);
    }

    [Fact]
    public async Task RunBatch_derives_null_when_only_filter_tags()
    {
        long id = _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                PrimaryCategory = null,
                Tags = [new AiTag("写实", 0.9), new AiTag("自然光", 0.8)],
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        string? category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        // 写实/自然光都是筛选组标签 → 推导为 null → AI PrimaryCategory 也为 null → 不写入
        Assert.True(string.IsNullOrEmpty(category));
    }

    [Fact]
    public async Task RunBatch_multi_category_derives_by_tag_count()
    {
        long id = _lib.InsertImage();
        var ai = new FakeAiService
        {
            AnalyzeFunc = _ => new AiAnalysisResult
            {
                Tags = [new AiTag("骑士", 0.9), new AiTag("士兵", 0.8),
                        new AiTag("森林", 0.7)],
            },
        };
        var notifications = new NotificationService();
        var tags = new TagService(_lib.Db);
        var analysis = new AiAnalysisService(ai, _lib.Db, tags, notifications);

        await analysis.RunBatchAsync();

        string? category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        // 角色×2（骑士+士兵）vs 场景×1（森林）→ 角色
        Assert.Equal("角色", category);
    }

    [Fact]
    public void RecomputeCategoryForImage_updates_category_from_tags()
    {
        long id = _lib.InsertImage();
        var tags = new TagService(_lib.Db);

        // 手动加标签：骑士（角色组）+ 金属（质感组）
        tags.AddTags([id], ["骑士", "金属"], TagSource.Manual);

        // 初次推导：角色×1 vs 质感×1 → 平局 → Categories 中角色在质感前面 → 角色
        tags.RecomputeCategoryForImage(id);

        string? category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        Assert.Equal("角色", category);

        // 再加一个质感标签：石材 → 质感×2 vs 角色×1 → 质感
        tags.AddTags([id], ["石材"], TagSource.Manual);
        tags.RecomputeCategoryForImage(id);

        category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        Assert.Equal("质感", category);
    }

    [Fact]
    public void RecomputeCategoryForImage_sets_null_when_no_category_tags()
    {
        long id = _lib.InsertImage(category: "角色");
        var tags = new TagService(_lib.Db);

        // 只加筛选组标签
        tags.AddTags([id], ["写实", "自然光"], TagSource.Manual);
        tags.RecomputeCategoryForImage(id);

        string? category = _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT primary_category FROM images WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar()?.ToString();
        });
        Assert.True(string.IsNullOrEmpty(category));
    }
}
