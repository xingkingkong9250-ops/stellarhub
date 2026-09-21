using QL.World.Data;
using QL.World.Services.Ai;

namespace QL.World.Services;

/// <summary>
/// Semantic search service (#15). Loads all image_embeddings into memory once,
/// then ranks by cosine similarity to a query vector (text embedding from
/// IAiService.EmbedTextAsync or an existing image embedding). Cache is invalidated
/// when AiAnalysisService.EmbeddingsUpdated fires.
///
/// Performance: 1000 × 512 × 4B = 2MB resident; cosine + sort is ~500K multiplies,
/// comfortably &lt;100ms on commodity hardware.
/// </summary>
public sealed class SemanticSearchService
{
    /// <summary>Top-N similar images returned by each query. PRD: 千级库 &lt;100ms.</summary>
    public const int TopN = 50;

    private readonly DatabaseService _db;
    private readonly IAiService _ai;

    private readonly object _cacheLock = new();
    private Dictionary<long, float[]>? _cache;

    public SemanticSearchService(DatabaseService db, IAiService ai)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ai);
        _db = db;
        _ai = ai;
    }

    /// <summary>True when at least one image has a stored embedding (UI uses this to enable 「语义」 mode and 「找相似」 commands).</summary>
    public bool HasAnyEmbeddings() => LoadCache().Count > 0;

    /// <summary>
    /// Text→image semantic search. Delegates to IAiService.EmbedTextAsync to compute
    /// the query vector, then ranks by cosine similarity. Returns null when the AI
    /// backend cannot produce text embeddings (Disabled / Local in M3-E1) —
    /// the caller (TopBarViewModel) decides how to surface that.
    /// </summary>
    public async Task<IReadOnlyList<long>?> SearchByTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        float[]? query = await _ai.EmbedTextAsync(text, ct);
        return query is null ? null : SearchByEmbedding(query);
    }

    /// <summary>
    /// Image→image similar search. Reads the source image's embedding from the
    /// cache (or DB), excludes the source itself, returns top-N similar ids.
    /// Works in any AI mode — no live model call needed.
    /// </summary>
    public IReadOnlyList<long>? SearchByImageId(long imageId)
    {
        var cache = LoadCache();
        if (cache.Count == 0) return null;
        if (!cache.TryGetValue(imageId, out float[]? query) || query.Length == 0) return null;
        return SearchByEmbedding(query, excludeId: imageId);
    }

    /// <summary>
    /// Core: rank all cached embeddings by cosine similarity to <paramref name="query"/>,
    /// return top-N image ids in descending order. Optionally exclude the source id
    /// (used by image→image so the source doesn't rank #1 against itself).
    /// Returns null when the cache is empty.
    /// </summary>
    public IReadOnlyList<long>? SearchByEmbedding(float[] query, long? excludeId = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) return null;
        var cache = LoadCache();
        if (cache.Count == 0) return null;

        float queryNorm = MathF.Sqrt(SumSquares(query));
        if (queryNorm == 0f) return null;

        var scored = new List<(long Id, float Score)>(cache.Count);
        foreach ((long id, float[] vec) in cache)
        {
            if (id == excludeId) continue;
            if (vec.Length == 0) continue;
            float vecNorm = MathF.Sqrt(SumSquares(vec));
            if (vecNorm == 0f) continue;
            float dot = Dot(query, vec);
            scored.Add((id, dot / (queryNorm * vecNorm)));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        int take = Math.Min(TopN, scored.Count);
        var result = new List<long>(take);
        for (int i = 0; i < take; i++) result.Add(scored[i].Id);
        return result;
    }

    /// <summary>Called by AiAnalysisService.EmbeddingsUpdated to invalidate the cache.</summary>
    public void Invalidate()
    {
        lock (_cacheLock) _cache = null;
    }

    // ── helpers ────────────────────────────────────────────────────────

    private Dictionary<long, float[]> LoadCache()
    {
        lock (_cacheLock)
        {
            if (_cache is not null) return _cache;
            var dict = new Dictionary<long, float[]>();
            _db.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT image_id, dim, embedding FROM image_embeddings WHERE dim > 0;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    int dim = reader.GetInt32(1);
                    byte[] blob = (byte[])reader[2];
                    if (blob is null || blob.Length != dim * sizeof(float)) continue;
                    var vec = new float[dim];
                    Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
                    dict[id] = vec;
                }
            });
            _cache = dict;
            return dict;
        }
    }

    private static float SumSquares(float[] v)
    {
        float sum = 0f;
        for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
        return sum;
    }

    private static float Dot(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }
}
