using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using QL.World.Models.Ai;
using QL.World.Services.Ai;
using QL.World.Services.Ai.Local;
using QL.World.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Tests.Ai.Local;

public sealed class LocalAiServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));

    public LocalAiServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string ModelsDir
    {
        get
        {
            Directory.CreateDirectory(Path.Combine(_directory, "models"));
            return Path.Combine(_directory, "models");
        }
    }

    private string WriteModelFile(byte[] bytes)
    {
        string path = Path.Combine(ModelsDir, ModelManifest.ModelFileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>224×224 vertical gradient PNG: the stub output = first 512 preprocessed values.</summary>
    private string WriteGradientImage()
    {
        string imagePath = Path.Combine(_directory, "grad.png");
        using (var image = new Image<Rgb24>(224, 224))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < 224; y++)
                {
                    Span<Rgb24> row = accessor.GetRowSpan(y);
                    byte value = (byte)(y * 255 / 223);
                    for (int x = 0; x < 224; x++)
                        row[x] = new Rgb24(value, 0, 0);
                }
            });
            image.Save(imagePath, new PngEncoder());
        }
        return imagePath;
    }

    [Fact]
    public async Task Missing_model_is_unavailable_and_untestable()
    {
        var ai = new LocalAiService(ModelsDir);

        Assert.Equal("Local", ai.Mode);
        Assert.True(ai.SupportsEmbeddings);
        Assert.False(ai.IsAvailable);
        Assert.False(await ai.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_model_yields_error_result_without_touching_session()
    {
        var ai = new LocalAiService(
            ModelsDir,
            sessionFactory: _ => throw new InvalidOperationException("session must not be created"));

        string image = TestImageFactory.CreatePng(_directory, "a.png");
        AiAnalysisResult result = await ai.AnalyzeImageAsync(image, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("本地模型未下载", result.ErrorMessage);
    }

    [Fact]
    public async Task Session_factory_failure_surfaces_as_error_result()
    {
        WriteModelFile([1, 2, 3, 4]);
        var ai = new LocalAiService(
            ModelsDir,
            sessionFactory: _ => throw new InvalidOperationException("boom"));

        string image = TestImageFactory.CreatePng(_directory, "a.png");
        AiAnalysisResult result = await ai.AnalyzeImageAsync(image, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("boom", result.ErrorMessage);
        Assert.Null(result.Embedding);
    }

    [Fact]
    public async Task Stub_model_produces_embedding_equal_to_first_512_preprocessed_values()
    {
        WriteModelFile(MiniOnnx.BuildClipStubModel());
        string imagePath = WriteGradientImage();

        var ai = new LocalAiService(ModelsDir);
        Assert.True(ai.IsAvailable);

        AiAnalysisResult result = await ai.AnalyzeImageAsync(imagePath, CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal(ModelManifest.ModelId, result.ModelId);
        Assert.NotNull(result.Embedding);
        Assert.Equal(512, result.Embedding.Length);
        Assert.Equal(ClipPreprocessor.FromFile(imagePath)[..512], result.Embedding);
    }

    [Fact]
    public async Task Stub_model_ranks_against_synthetic_text_index()
    {
        WriteModelFile(MiniOnnx.BuildClipStubModel());
        string imagePath = WriteGradientImage();

        float[] expected = ClipPreprocessor.FromFile(imagePath);
        float[] direction = Normalize(expected[..512]);
        float[] orthogonal = new float[512];
        orthogonal[0] = 1f;
        WriteSyntheticTextIndex(ModelsDir, direction, orthogonal);

        var ai = new LocalAiService(ModelsDir);
        AiAnalysisResult result = await ai.AnalyzeImageAsync(imagePath, CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal("贴图/纹理", result.PrimaryCategory);
        AiTag tag = Assert.Single(result.Tags);
        Assert.Equal("硬表面", tag.Tag);
        Assert.True(tag.Confidence > 0.99, $"confidence={tag.Confidence}");
    }

    [Fact]
    public async Task TestConnection_respects_injected_state_check()
    {
        var ready = new LocalAiService(ModelsDir, localStateCheck: () => LocalModelState.Ready);
        Assert.True(await ready.TestConnectionAsync(CancellationToken.None));

        var corrupted = new LocalAiService(ModelsDir, localStateCheck: () => LocalModelState.Corrupted);
        Assert.False(await corrupted.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EmbedText_returns_null_without_creating_session()
    {
        // LocalAiService.EmbedTextAsync 在 M3-E1 中应直接返回 null，
        // 不创建 InferenceSession（本地 CLIP text encoder 延后到 M3-E2）。
        var ai = new LocalAiService(
            ModelsDir,
            sessionFactory: _ => throw new InvalidOperationException("session must not be created"));

        float[]? emb = await ai.EmbedTextAsync("硬表面金属质感", CancellationToken.None);

        Assert.Null(emb);
    }

    private static float[] Normalize(float[] vector)
    {
        float norm = MathF.Sqrt(vector.Sum(x => x * x));
        return [.. vector.Select(x => x / norm)];
    }

    /// <summary>
    /// Writes a synthetic 512-dim text-embeddings vocabulary: the first
    /// category/tag term is aligned with the stub model's output direction
    /// (cosine 1.0), the second is a unit basis vector (cosine ≈ 0.04 &lt; 0.2).
    /// </summary>
    private static void WriteSyntheticTextIndex(string modelsDir, float[] direction, float[] orthogonal)
    {
        var payload = new
        {
            promptTemplateVersion = 1,
            categories = new object[]
            {
                new { name = "贴图/纹理", prompt = "p", embedding = direction },
                new { name = "实拍参考", prompt = "p", embedding = orthogonal },
            },
            tagGroups = new
            {
                风格 = new object[]
                {
                    new { name = "硬表面", prompt = "p", embedding = direction },
                    new { name = "有机形态", prompt = "p", embedding = orthogonal },
                },
                题材 = Array.Empty<object>(),
                材质 = Array.Empty<object>(),
                色调 = Array.Empty<object>(),
            },
        };

        string target = Path.Combine(modelsDir, ModelManifest.TextEmbeddingsFileName);
        File.WriteAllText(target, JsonSerializer.Serialize(payload));
    }
}
