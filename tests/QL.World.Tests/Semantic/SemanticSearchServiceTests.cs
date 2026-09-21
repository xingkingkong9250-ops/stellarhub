using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests.Semantic;

public sealed class SemanticSearchServiceTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly FakeAiService _ai = new();
    private readonly SemanticSearchService _service;

    public SemanticSearchServiceTests()
    {
        _service = new SemanticSearchService(_lib.Db, _ai);
    }

    public void Dispose() => _lib.Dispose();

    [Fact]
    public void HasAnyEmbeddings_false_when_empty()
    {
        Assert.False(_service.HasAnyEmbeddings());
    }

    [Fact]
    public void HasAnyEmbeddings_true_after_insert()
    {
        long id = _lib.InsertImage();
        WriteEmbedding(id, [1f, 0f]);
        Assert.True(_service.HasAnyEmbeddings());
    }

    [Fact]
    public async Task SearchByText_returns_topN_in_descending_cosine_order()
    {
        // 三个向量：[1,0] 与 query 完全对齐 (cos=1)；[0.7,0.7] cos≈0.707；[0,1] cos=0。
        // TopN=50 > 3，所以全 3 个都返回，按余弦降序排列。
        long idA = _lib.InsertImage();
        long idB = _lib.InsertImage();
        long idC = _lib.InsertImage();
        WriteEmbedding(idA, [1f, 0f]);
        WriteEmbedding(idB, [0.7f, 0.7f]);
        WriteEmbedding(idC, [0f, 1f]);

        _ai.EmbedTextFunc = _ => [1f, 0f];

        IReadOnlyList<long>? ids = await _service.SearchByTextAsync("any", CancellationToken.None);

        Assert.NotNull(ids);
        Assert.Equal(new long[] { idA, idB, idC }, ids!.ToArray());
    }

    [Fact]
    public async Task SearchByText_truncates_at_TopN()
    {
        // 插 60 个完全相同的向量（cos=1，平局按字典 id 升序，因为 Sort 稳定）。
        for (int i = 0; i < 60; i++)
        {
            long id = _lib.InsertImage();
            WriteEmbedding(id, [1f, 0f]);
        }
        _ai.EmbedTextFunc = _ => [1f, 0f];

        IReadOnlyList<long>? ids = await _service.SearchByTextAsync("x", CancellationToken.None);

        Assert.NotNull(ids);
        Assert.Equal(SemanticSearchService.TopN, ids!.Count);
    }

    [Fact]
    public async Task SearchByText_returns_null_when_ai_cannot_embed()
    {
        // 默认 EmbedTextFunc=null（FakeAiService 不支持文本 embedding）。
        long id = _lib.InsertImage();
        WriteEmbedding(id, [1f, 0f]);

        IReadOnlyList<long>? ids = await _service.SearchByTextAsync("text", CancellationToken.None);

        Assert.Null(ids);
    }

    [Fact]
    public void SearchByImageId_excludes_source_and_returns_similar()
    {
        long source = _lib.InsertImage();
        long near = _lib.InsertImage();
        long far = _lib.InsertImage();
        WriteEmbedding(source, [1f, 0f]);    // 源
        WriteEmbedding(near, [0.9f, 0.1f]); // 最相似
        WriteEmbedding(far, [0f, 1f]);      // 最不相似

        IReadOnlyList<long>? ids = _service.SearchByImageId(source);

        Assert.NotNull(ids);
        Assert.DoesNotContain(source, ids!.ToArray());
        Assert.Equal(new long[] { near, far }, ids!.ToArray());
    }

    [Fact]
    public void SearchByImageId_returns_null_when_source_has_no_embedding()
    {
        long other = _lib.InsertImage();
        WriteEmbedding(other, [1f, 0f]);
        long noEmbId = _lib.InsertImage();

        Assert.Null(_service.SearchByImageId(noEmbId));
    }

    [Fact]
    public void SearchByImageId_returns_null_when_cache_empty()
    {
        Assert.Null(_service.SearchByImageId(1L));
    }

    [Fact]
    public void Invalidate_drops_cache_so_new_embeddings_are_visible()
    {
        long id1 = _lib.InsertImage();
        WriteEmbedding(id1, [1f, 0f]);
        Assert.True(_service.HasAnyEmbeddings());

        // 加新 embedding 但缓存未失效——SearchByImageId 应使用旧缓存。
        long id2 = _lib.InsertImage();
        WriteEmbedding(id2, [0f, 1f]);
        IReadOnlyList<long>? ids1 = _service.SearchByImageId(id1);
        Assert.NotNull(ids1);
        Assert.DoesNotContain(id2, ids1!.ToArray()); // 缓存未含 id2

        _service.Invalidate();
        IReadOnlyList<long>? ids2 = _service.SearchByImageId(id1);
        Assert.NotNull(ids2);
        Assert.Contains(id2, ids2!.ToArray());
    }

    [Fact]
    public async Task Empty_text_query_returns_null()
    {
        long id = _lib.InsertImage();
        WriteEmbedding(id, [1f, 0f]);
        Assert.Null(await _service.SearchByTextAsync("", CancellationToken.None));
        Assert.Null(await _service.SearchByTextAsync("   ", CancellationToken.None));
    }

    [Fact]
    public void SearchByEmbedding_skips_zero_norm_vectors()
    {
        long idA = _lib.InsertImage();
        long idB = _lib.InsertImage();
        WriteEmbedding(idA, [1f, 0f]);
        WriteEmbedding(idB, [0f, 0f]); // 零范数，应被跳过

        IReadOnlyList<long>? ids = _service.SearchByEmbedding([1f, 0f]);

        Assert.NotNull(ids);
        Assert.Equal(new long[] { idA }, ids!.ToArray());
    }

    // ── helpers ────────────────────────────────────────────────────────

    private void WriteEmbedding(long imageId, float[] embedding)
    {
        byte[] blob = new byte[embedding.Length * sizeof(float)];
        System.Buffer.BlockCopy(embedding, 0, blob, 0, blob.Length);

        _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO image_embeddings (image_id, model_id, dim, embedding, created_at)
                VALUES ($id, $model, $dim, $blob, $now);
                """;
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.Parameters.AddWithValue("$model", "fake");
            cmd.Parameters.AddWithValue("$dim", embedding.Length);
            cmd.Parameters.AddWithValue("$blob", blob);
            cmd.Parameters.AddWithValue("$now", "2026-09-20 00:00:00");
            cmd.ExecuteNonQuery();
        });
    }
}
