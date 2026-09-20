using System.IO;
using System.Text.Json;
using QL.World.Services.Ai.Local;

namespace QL.World.Tests.Ai.Local;

public sealed class ClipTextIndexTests
{
    private static readonly string SampleJson =
        """
        {
          "promptTemplateVersion": 1,
          "categories": [
            { "name": "贴图/纹理", "prompt": "a", "embedding": [1.0, 0.0, 0.0, 0.0] },
            { "name": "环境/场景", "prompt": "b", "embedding": [0.0, 1.0, 0.0, 0.0] }
          ],
          "tagGroups": {
            "材质": [
              { "name": "金属", "prompt": "x", "embedding": [0.9, 0.1, 0.0, 0.0] },
              { "name": "木材", "prompt": "y", "embedding": [0.1, 0.9, 0.0, 0.0] },
              { "name": "锈蚀", "prompt": "z", "embedding": [0.1, 0.1, 0.9, 0.0] }
            ]
          }
        }
        """;

    [Fact]
    public void Loads_camelCase_json_and_exposes_version()
    {
        ClipTextIndex index = ClipTextIndex.LoadJson(SampleJson);
        Assert.Equal(1, index.PromptTemplateVersion);
    }

    [Fact]
    public void RankCategory_returns_best_above_threshold()
    {
        ClipTextIndex index = ClipTextIndex.LoadJson(SampleJson);
        float[] emb = [0.95f, 0.05f, 0.0f, 0.0f];

        string? category = index.RankCategory(emb);

        Assert.Equal("贴图/纹理", category);
    }

    [Fact]
    public void RankCategory_returns_null_when_below_threshold()
    {
        ClipTextIndex index = ClipTextIndex.LoadJson(SampleJson);
        // Nearly orthogonal to both category terms → cos ≈ 0.01 < 0.20.
        float[] emb = [0.01f, 0.01f, 0.0f, 0.99f];

        Assert.Null(index.RankCategory(emb));
    }

    [Fact]
    public void RankTags_orders_by_score_descending()
    {
        ClipTextIndex index = ClipTextIndex.LoadJson(SampleJson);
        float[] emb = [0.9f, 0.1f, 0.0f, 0.0f];

        var tags = index.RankTags(emb);

        Assert.Equal("金属", tags[0].Tag);
        Assert.True(tags[0].Confidence >= tags[^1].Confidence);
    }

    [Fact]
    public void RankTags_respects_max_per_group()
    {
        ClipTextIndex index = ClipTextIndex.LoadJson(SampleJson);
        float[] emb = [1.0f, 1.0f, 1.0f, 0.0f];

        var tags = index.RankTags(emb);

        Assert.True(tags.Count <= 3);
    }

    [Fact]
    public void EnsureDistributed_copies_embedded_resource()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string path = ClipTextIndex.EnsureDistributed(dir);

            Assert.True(File.Exists(path));
            Assert.Equal("text-embeddings-clip-vit-b32.json", Path.GetFileName(path));
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.GetProperty("categories").GetArrayLength() > 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void EnsureDistributed_is_idempotent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string first = ClipTextIndex.EnsureDistributed(dir);
            // Create a "user-provided" file to prove it is not overwritten.
            File.WriteAllText(first, "{}");
            string second = ClipTextIndex.EnsureDistributed(dir);

            Assert.Equal(first, second);
            Assert.Equal("{}", File.ReadAllText(second));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }
}
