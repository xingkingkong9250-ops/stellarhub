using System.IO;
using System.Text.Json;
using QL.World.Models.Taxonomy;
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
            // Overwrite with a valid file at ExpectedPromptTemplateVersion to prove
            // it is not re-extracted (v2 after the M12 JSON regeneration).
            string userJson = "{\"promptTemplateVersion\":" +
                QL.World.Services.Ai.Local.ClipTextIndex.ExpectedPromptTemplateVersion +
                ",\"categories\":[],\"tagGroups\":{}}";
            File.WriteAllText(first, userJson);
            string second = ClipTextIndex.EnsureDistributed(dir);

            Assert.Equal(first, second);
            Assert.Equal(userJson, File.ReadAllText(second));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void EnsureDistributed_re_extracts_outdated_version()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "text-embeddings-clip-vit-b32.json");
            // Write an outdated v0 file — below ExpectedPromptTemplateVersion.
            File.WriteAllText(target, """{"promptTemplateVersion":0,"categories":[],"tagGroups":{}}""");

            string path = ClipTextIndex.EnsureDistributed(dir);

            Assert.Equal(target, path);
            // Re-extracted from embedded resource: real categories present.
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.GetProperty("categories").GetArrayLength() > 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    // ── M12: embedded v2 resource ↔ TagTaxonomy contract ───────────────────

    /// <summary>
    /// The distributed JSON (regenerated by ClipSpike gen) must stay in lockstep
    /// with TagTaxonomy: same 6 categories, same 11 CLIP-mapped groups (the
    /// 文件格式/色彩倾向 groups have no CLIP vocabulary), same ordered tag names.
    /// </summary>
    [Fact]
    public void Embedded_resource_matches_TagTaxonomy_vocabulary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string path = ClipTextIndex.EnsureDistributed(dir);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            Assert.Equal(ClipTextIndex.ExpectedPromptTemplateVersion,
                root.GetProperty("promptTemplateVersion").GetInt32());
            Assert.Equal(2, ClipTextIndex.ExpectedPromptTemplateVersion);

            // Categories: exact ordered match with the 6 TagTaxonomy categories.
            string[] jsonCats = [.. root.GetProperty("categories").EnumerateArray()
                .Select(e => e.GetProperty("name").GetString() ?? string.Empty)];
            Assert.Equal(TagTaxonomy.Categories, jsonCats);

            // Groups: all TagTaxonomy groups except 文件格式/色彩倾向.
            string[] expectedGroups = [.. TagTaxonomy.AllGroups
                .Where(g => g != TagTaxonomy.GroupFileFormat && g != TagTaxonomy.GroupColorTone)];
            string[] jsonGroups = [.. root.GetProperty("tagGroups").EnumerateObject()
                .Select(p => p.Name)];
            Assert.Equal(expectedGroups, jsonGroups);

            // Per-group tags: exact ordered name match + every embedding is 512-dim.
            foreach (string group in expectedGroups)
            {
                JsonElement terms = root.GetProperty("tagGroups").GetProperty(group);
                string[] names = [.. terms.EnumerateArray()
                    .Select(e => e.GetProperty("name").GetString() ?? string.Empty)];
                Assert.Equal(TagTaxonomy.TagsByGroup[group], names);

                foreach (JsonElement term in terms.EnumerateArray())
                {
                    Assert.Equal(512, term.GetProperty("embedding").GetArrayLength());
                }
            }

            // 6 + 168 = 174 zero-shot terms.
            int termCount = jsonCats.Length + expectedGroups.Sum(g => TagTaxonomy.TagsByGroup[g].Count);
            Assert.Equal(174, termCount);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }
}
