using System.IO;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// 0.16.0 DUP-C: 全库查重扫描——L1 聚类、L2 模型隔离、候选表清理/保留、
/// 缩略图 phash 补算、进度与取消。
/// </summary>
public sealed class DuplicateScanServiceTests
{
    [Fact]
    public async Task Transitive_closure_groups_chain_into_one_exact_group()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        long third = library.InsertImage("c.png");
        long isolated = library.InsertImage("d.png");

        SetPhash(library, first, 0UL);
        SetPhash(library, second, 1UL);          // 与 first 距离 1
        SetPhash(library, third, 3UL);           // 与 second 距离 2、与 first 距离 2
        SetPhash(library, isolated, ulong.MaxValue);

        var service = new DuplicateScanService(library.Db);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.Equal(1, result.GroupCount);
        Assert.Equal(1, result.ExactGroupCount);
        Assert.Equal(0, result.VariantGroupCount);
        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(4, result.ImagesWithPhash);
        Assert.Equal(0, result.ImagesWithoutPhash);
        Assert.False(result.Cancelled);

        List<CandidateRow> rows = ReadCandidates(library);
        Assert.Equal(3, rows.Count);
        Assert.Single(rows.Select(row => row.GroupId).Distinct());
        Assert.All(rows, row =>
        {
            Assert.Equal("exact", row.Kind);
            Assert.Equal("L1", row.Level);
            Assert.Equal("pending", row.Status);
        });
        Assert.DoesNotContain(rows, row => row.ImageId == isolated);
    }

    [Fact]
    public async Task L2_vectors_only_compare_within_same_model_id()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        long third = library.InsertImage("c.png");

        SetPhash(library, first, 0UL);
        SetPhash(library, second, ulong.MaxValue);
        SetPhash(library, third, 0xAAAAAAAAAAAAAAAAUL);

        InsertEmbedding(library, first, "local-model", [1f, 0f, 0f]);
        InsertEmbedding(library, second, "local-model", [1f, 0f, 0f]);
        InsertEmbedding(library, third, "cloud-model", [1f, 0f, 0f]);

        var service = new DuplicateScanService(library.Db);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.Equal(1, result.GroupCount);
        Assert.Equal(0, result.ExactGroupCount);
        Assert.Equal(1, result.VariantGroupCount);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(3, result.VectorCount);

        List<CandidateRow> rows = ReadCandidates(library);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("variant", row.Kind);
            Assert.Equal("L2", row.Level);
        });
        Assert.DoesNotContain(rows, row => row.ImageId == third);
    }

    [Fact]
    public async Task Rescan_replaces_pending_but_keeps_resolved_and_dismissed()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0UL);

        var service = new DuplicateScanService(library.Db);
        await service.ScanAsync();
        Assert.Equal(2, ReadCandidates(library).Count);

        SetStatus(library, "resolved", first);
        SetStatus(library, "dismissed", second);

        // 改成两张互不相同的图：新结果为空，旧 pending 清空但不能动历史状态。
        SetPhash(library, second, ulong.MaxValue);
        DuplicateScanResult empty = await service.ScanAsync();
        Assert.Equal(0, empty.GroupCount);
        List<CandidateRow> afterEmpty = ReadCandidates(library);
        Assert.Equal(2, afterEmpty.Count);
        Assert.DoesNotContain(afterEmpty, row => row.Status == "pending");

        // 再改回同图：新一轮 pending 写入，历史两行仍在。
        SetPhash(library, second, 0UL);
        DuplicateScanResult again = await service.ScanAsync();
        Assert.Equal(1, again.GroupCount);
        List<CandidateRow> finalRows = ReadCandidates(library);
        Assert.Equal(4, finalRows.Count);
        Assert.Equal(2, finalRows.Count(row => row.Status == "pending"));
        Assert.Equal(1, finalRows.Count(row => row.Status == "resolved"));
        Assert.Equal(1, finalRows.Count(row => row.Status == "dismissed"));
    }

    [Fact]
    public async Task Backfills_missing_phash_from_thumb_webp_and_counts_unavailable()
    {
        using var library = new TestLibrary();
        string hash = "0123456789abcdef0123456789abcdef";
        long withThumb = library.InsertImage("a.png", hash);
        long withoutThumb = library.InsertImage("b.png", "fedcba9876543210fedcba9876543210");

        string thumbRoot = Path.Combine(library.DirectoryRoot, "thumbnails");
        string thumbPath = TestImageFactory.CreateGradientWebp(
            Path.Combine(thumbRoot, hash[..2]),
            $"{hash}_thumb.webp");

        var service = new DuplicateScanService(library.Db, thumbnailRoot: thumbRoot);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.Equal(1, result.PhashBackfilled);
        Assert.Equal(1, result.ImagesWithPhash);
        Assert.Equal(1, result.ImagesWithoutPhash);

        string? stored = ReadPhash(library, withThumb);
        Assert.NotNull(stored);
        ulong? expected = new PerceptualHashService().TryCompute(thumbPath);
        Assert.NotNull(expected);
        Assert.Equal(PerceptualHashService.ToHex(expected!.Value), stored);
        Assert.Null(ReadPhash(library, withoutThumb));

        // 第二次扫描没有 NULL 行，不再重复补算。
        DuplicateScanResult second = await service.ScanAsync();
        Assert.Equal(0, second.PhashBackfilled);
        Assert.Equal(1, second.ImagesWithPhash);
        Assert.Equal(1, second.ImagesWithoutPhash);
    }

    [Fact]
    public async Task Progress_reports_pairing_and_persist_phases()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 2UL);

        var progress = new CollectingProgress();
        var service = new DuplicateScanService(library.Db);
        await service.ScanAsync(progress);

        Assert.Contains(progress.Items, item => item.Phase == DuplicateScanPhase.PhashPairing);
        DuplicateScanProgress persist = Assert.Single(
            progress.Items, item => item.Phase == DuplicateScanPhase.Persist);
        Assert.Equal(1, persist.Total);
        Assert.Equal(1, persist.Processed);
    }

    [Fact]
    public async Task Cancellation_writes_no_candidates()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0UL);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = new DuplicateScanService(library.Db);
        DuplicateScanResult result = await service.ScanAsync(cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.GroupCount);
        Assert.Empty(ReadCandidates(library));
    }

    // ── 0.16.0 DUP-E: L3 VLM 终审 ─────────────────────────────────────

    [Fact]
    public async Task L3_phash_boundary_pair_confirmed_by_vlm_persists_as_exact()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0xFFFUL);   // 距离 12，落在 11~18 边界带

        var vlm = new StubVlm((_, _) => (true, 0.9));
        var service = new DuplicateScanService(library.Db, vlm: vlm);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.True(result.L3Available);
        Assert.Equal(1, result.L3PairsReviewed);
        Assert.Equal(1, result.L3ConfirmedPairs);
        Assert.Equal(0, result.L3RejectedPairs);
        Assert.Equal(1, result.GroupCount);
        Assert.Equal(1, result.ExactGroupCount);
        Assert.Equal(1, result.L3GroupCount);
        Assert.Single(vlm.Calls);

        List<CandidateRow> rows = ReadCandidates(library);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("exact", row.Kind);
            Assert.Equal("L3", row.Level);
        });
    }

    [Fact]
    public async Task L3_boundary_pair_rejected_by_vlm_is_discarded()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0xFFFUL);

        var vlm = new StubVlm((_, _) => (false, 0.95));
        var service = new DuplicateScanService(library.Db, vlm: vlm);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.Equal(1, result.L3PairsReviewed);
        Assert.Equal(0, result.L3ConfirmedPairs);
        Assert.Equal(1, result.L3RejectedPairs);
        Assert.Equal(0, result.GroupCount);
        Assert.Empty(ReadCandidates(library));
    }

    [Fact]
    public async Task L3_cosine_boundary_pair_confirmed_becomes_variant()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        // phash 差异最大 → 不进 pHash 边界带；仅余弦 0.9 落在 0.88~0.92。
        SetPhash(library, first, 0UL);
        SetPhash(library, second, ulong.MaxValue);
        InsertEmbedding(library, first, "m", [1f, 0f, 0f]);
        InsertEmbedding(library, second, "m", [0.9f, 0.4358899f, 0f]);

        var vlm = new StubVlm((_, _) => (true, 0.85));
        var service = new DuplicateScanService(library.Db, vlm: vlm);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.Equal(1, result.L3ConfirmedPairs);
        Assert.Equal(1, result.GroupCount);
        Assert.Equal(1, result.VariantGroupCount);
        Assert.Equal(1, result.L3GroupCount);

        List<CandidateRow> rows = ReadCandidates(library);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("variant", row.Kind);
            Assert.Equal("L3", row.Level);
        });
    }

    [Fact]
    public async Task L3_is_skipped_silently_when_vlm_unavailable()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        long third = library.InsertImage("c.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0xFFFUL);   // 边界带，无 VLM 应忽略
        SetPhash(library, third, 1UL);        // 与 first 距离 1，L1 照常入组

        var service = new DuplicateScanService(library.Db);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.False(result.L3Available);
        Assert.Equal(0, result.L3PairsReviewed);
        Assert.Equal(1, result.GroupCount);
        Assert.Equal(1, result.ExactGroupCount);

        List<CandidateRow> rows = ReadCandidates(library);
        Assert.DoesNotContain(rows, row => row.ImageId == second);
        Assert.All(rows, row => Assert.Equal("L1", row.Level));
    }

    [Fact]
    public async Task L3_no_verdict_drops_pair_without_counting_rejection()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0xFFFUL);

        // Confidence=0 表示「无判定」（未配置/网络/解析失败），不是否决。
        var vlm = new StubVlm((_, _) => (false, 0));
        var service = new DuplicateScanService(library.Db, vlm: vlm);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.Equal(1, result.L3PairsReviewed);
        Assert.Equal(0, result.L3ConfirmedPairs);
        Assert.Equal(0, result.L3RejectedPairs);
        Assert.Equal(0, result.GroupCount);
        Assert.Empty(ReadCandidates(library));
    }

    [Fact]
    public async Task L3_does_not_send_confident_l1_pairs_to_vlm()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 3UL);       // 距离 2 ≤10，已确定同图

        var vlm = new StubVlm((_, _) => (false, 0.99));
        var service = new DuplicateScanService(library.Db, vlm: vlm);
        DuplicateScanResult result = await service.ScanAsync();

        Assert.Empty(vlm.Calls);
        Assert.Equal(1, result.GroupCount);
        Assert.All(ReadCandidates(library), row => Assert.Equal("L1", row.Level));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static void SetPhash(TestLibrary library, long imageId, ulong hash) =>
        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "UPDATE images SET phash = $phash WHERE id = $id;";
            command.Parameters.AddWithValue("$phash", PerceptualHashService.ToHex(hash));
            command.Parameters.AddWithValue("$id", imageId);
            command.ExecuteNonQuery();
        });

    private static void SetStatus(TestLibrary library, string status, long imageId) =>
        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                UPDATE duplicate_candidates SET status = $status, updated_at = $now
                WHERE image_id = $id;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$now", "2026-10-27T00:00:00.000Z");
            command.Parameters.AddWithValue("$id", imageId);
            command.ExecuteNonQuery();
        });

    private static void InsertEmbedding(
        TestLibrary library, long imageId, string modelId, float[] vector) =>
        library.Db.Run(conn =>
        {
            byte[] blob = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

            using var command = conn.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO image_embeddings
                    (image_id, model_id, dim, embedding, created_at, source)
                VALUES
                    ($id, $model, $dim, $blob, $now, 'local');
                """;
            command.Parameters.AddWithValue("$id", imageId);
            command.Parameters.AddWithValue("$model", modelId);
            command.Parameters.AddWithValue("$dim", vector.Length);
            command.Parameters.AddWithValue("$blob", blob);
            command.Parameters.AddWithValue("$now", "2026-10-27T00:00:00.000Z");
            command.ExecuteNonQuery();
        });

    private static string? ReadPhash(TestLibrary library, long imageId) =>
        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT phash FROM images WHERE id = $id;";
            command.Parameters.AddWithValue("$id", imageId);
            return command.ExecuteScalar() as string;
        });

    private static List<CandidateRow> ReadCandidates(TestLibrary library) =>
        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT image_id, group_id, kind, level, score, status
                FROM duplicate_candidates
                ORDER BY image_id, id;
                """;
            var rows = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new CandidateRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.GetString(5)));
            }
            return rows;
        });

    private sealed record CandidateRow(
        long ImageId, string GroupId, string Kind, string Level, double Score, string Status);

    private sealed class StubVlm : IVlmImageCompare
    {
        private readonly Func<string, string, (bool Same, double Confidence)> _verdict;

        public StubVlm(Func<string, string, (bool Same, double Confidence)> verdict) =>
            _verdict = verdict;

        public List<(string A, string B)> Calls { get; } = [];

        public Task<(bool SameImage, double Confidence)> CompareImagesAsync(
            string pathA, string pathB, CancellationToken cancellationToken)
        {
            Calls.Add((pathA, pathB));
            return Task.FromResult(_verdict(pathA, pathB));
        }
    }

    private sealed class CollectingProgress : IProgress<DuplicateScanProgress>
    {
        private readonly object _gate = new();

        public List<DuplicateScanProgress> Items { get; } = [];

        public void Report(DuplicateScanProgress value)
        {
            lock (_gate)
                Items.Add(value);
        }
    }
}
