using System.IO;
using System.Numerics.Tensors;
using System.Reflection;
using System.Text.Json;
using QL.World.Models.Ai;

namespace QL.World.Services.Ai.Local;

/// <summary>One zero-shot term (category or tag) with its precomputed text embedding.</summary>
public sealed record EmbeddingTerm(string Name, string Prompt, float[] Embedding);

/// <summary>Zero-shot ranking result for one image embedding.</summary>
public sealed record ClipRankResult(string? PrimaryCategory, IReadOnlyList<AiTag> Tags);

/// <summary>
/// Loads the precomputed CLIP text embeddings JSON (distributed as an embedded
/// resource and unpacked into {dataRoot}\models\) and ranks an image embedding
/// against the category and tag-group vocabularies with pure cosine
/// similarity — no ONNX involved. The JSON uses camelCase keys.
/// </summary>
public sealed class ClipTextIndex
{
    private const float CategoryThreshold = 0.20f;
    private const float TagThreshold = 0.20f;
    private const int MaxTagsPerGroup = 3;

    private readonly IReadOnlyList<EmbeddingTerm> _categories;
    private readonly IReadOnlyList<(string Group, IReadOnlyList<EmbeddingTerm> Terms)> _tagGroups;

    private ClipTextIndex(
        int promptTemplateVersion,
        IReadOnlyList<EmbeddingTerm> categories,
        IReadOnlyList<(string, IReadOnlyList<EmbeddingTerm>)> tagGroups)
    {
        PromptTemplateVersion = promptTemplateVersion;
        _categories = categories;
        _tagGroups = tagGroups;
    }

    public int PromptTemplateVersion { get; }

    /// <summary>Embedded resource name of the distributed JSON.</summary>
    public const string ResourceName = "QL.World.Assets.Ml.text-embeddings-clip-vit-b32.json";

    /// <summary>
    /// Expected prompt template version. Tracks the version of the embedded
    /// JSON resource. When the JSON is regenerated via ClipSpike <c>gen</c>
    /// and the new resource is embedded, bump this constant — EnsureDistributed
    /// will re-extract outdated on-disk files. Currently 2 (M12 tag-driven
    /// taxonomy: 6 categories + 11 tag groups; regenerated from the bundled
    /// text_model.onnx with ClipSpike Vocab v2).
    /// </summary>
    public const int ExpectedPromptTemplateVersion = 2;

    /// <summary>
    /// Copies the embedded text-embeddings JSON into the models directory when
    /// missing, outdated, or corrupt. Returns the target path. A user-provided
    /// file at or above <see cref="ExpectedPromptTemplateVersion"/> is preserved.
    /// </summary>
    public static string EnsureDistributed(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        string target = Path.Combine(modelsDirectory, ModelManifest.TextEmbeddingsFileName);

        if (File.Exists(target))
        {
            // Preserve a user-provided file at the expected version; re-extract
            // when the on-disk file is outdated or unparseable.
            try
            {
                ClipTextIndex existing = LoadFromFile(target);
                if (existing.PromptTemplateVersion >= ExpectedPromptTemplateVersion)
                    return target;
            }
            catch
            {
                // Corrupt or unparseable — fall through and re-extract.
            }
        }

        Directory.CreateDirectory(modelsDirectory);
        Assembly assembly = typeof(ClipTextIndex).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using (FileStream output = File.Create(target))
            stream.CopyTo(output);
        return target;
    }

    public static ClipTextIndex LoadFromFile(string path) =>
        LoadJson(File.ReadAllText(path));

    public static ClipTextIndex LoadJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        int version = root.GetProperty("promptTemplateVersion").GetInt32();

        var categories = new List<EmbeddingTerm>();
        foreach (JsonElement element in root.GetProperty("categories").EnumerateArray())
            categories.Add(ReadTerm(element));

        // EnumerateObject preserves JSON order, keeping ranking deterministic.
        var tagGroups = new List<(string, IReadOnlyList<EmbeddingTerm>)>();
        foreach (JsonProperty group in root.GetProperty("tagGroups").EnumerateObject())
        {
            var terms = new List<EmbeddingTerm>();
            foreach (JsonElement element in group.Value.EnumerateArray())
                terms.Add(ReadTerm(element));
            tagGroups.Add((group.Name, terms));
        }

        return new ClipTextIndex(version, categories, tagGroups);
    }

    private static EmbeddingTerm ReadTerm(JsonElement element) =>
        new(
            element.GetProperty("name").GetString() ?? string.Empty,
            element.GetProperty("prompt").GetString() ?? string.Empty,
            [.. element.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle())]);

    public ClipRankResult Rank(float[] imageEmbedding) =>
        new(RankCategory(imageEmbedding), RankTags(imageEmbedding));

    /// <summary>Argmax over the categories; null when the best score is below threshold.</summary>
    public string? RankCategory(float[] imageEmbedding)
    {
        ArgumentNullException.ThrowIfNull(imageEmbedding);
        string? best = null;
        float bestScore = float.NegativeInfinity;
        foreach (EmbeddingTerm term in _categories)
        {
            float score = Cosine(imageEmbedding, term.Embedding);
            if (score > bestScore)
            {
                bestScore = score;
                best = term.Name;
            }
        }
        return bestScore >= CategoryThreshold ? best : null;
    }

    /// <summary>
    /// Within each tag group, keeps the top terms (at most 3) whose cosine is
    /// at or above the threshold; confidence is the raw cosine clamped to 0–1.
    /// </summary>
    public IReadOnlyList<AiTag> RankTags(float[] imageEmbedding)
    {
        ArgumentNullException.ThrowIfNull(imageEmbedding);
        var tags = new List<AiTag>();
        foreach ((string _, IReadOnlyList<EmbeddingTerm> terms) in _tagGroups)
        {
            var best = terms
                .Select(t => (Term: t, Score: Cosine(imageEmbedding, t.Embedding)))
                .Where(x => x.Score >= TagThreshold)
                .OrderByDescending(x => x.Score)
                .Take(MaxTagsPerGroup);
            foreach (var (term, score) in best)
                tags.Add(new AiTag(term.Name, Math.Clamp(score, 0f, 1f)));
        }
        return tags;
    }

    private static float Cosine(float[] image, float[] term)
    {
        // Dimension mismatch (e.g. foreign vocabulary) ranks as no similarity.
        return image.Length > 0 && image.Length == term.Length
            ? TensorPrimitives.CosineSimilarity(image, term)
            : 0f;
    }
}
