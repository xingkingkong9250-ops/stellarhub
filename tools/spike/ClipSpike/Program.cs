using System.Numerics.Tensors;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using static ClipSpike.Vocab;
namespace ClipSpike;

public static class Program
{
    private static readonly string ModelDir =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models");
    private static string ModelPath(string name) => Path.GetFullPath(Path.Combine(ModelDir, name));

    private const string Repo = "Xenova/clip-vit-base-patch32";
    private const string TokenizerRepo = "openai/clip-vit-base-patch32";

    /// <summary>text-embeddings JSON is written with camelCase keys.</summary>
    internal static readonly System.Text.Json.JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { PrintUsage(); return 2; }
            switch (args[0])
            {
                case "download":
                    if (args.Length < 2) { PrintUsage(); return 2; }
                    return await CmdDownload(args[1], args.Contains("--quantized"));
                case "verify":
                    return CmdVerify(args[1]);
                case "bench":
                    return CmdBench(args[1], args.Length > 2 ? args[2] : ".");
                case "accuracy":
                    return CmdAccuracy(args[1], args.Length > 2 ? args[2] : ".");
                case "gen":
                    return CmdGenTextEmbeddings(args[1],
                        args.Length > 2 ? args[2] : "text-embeddings-clip-vit-b32.json");
                case "sanity":
                    return CmdSanity(args[1]);
                case "synth":
                    return CmdSynth(args.Length > 1 ? args[1] : "spike-images", 24);
                case "compare":
                    return CmdCompare(args[1], args.Length > 2 ? args[2] : "spike-images");
                default:
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[spike] ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage() => Console.WriteLine("""
        Usage:
          download <vision|text|tokenizer> [--quantized]
          verify   <model.onnx>
          bench    <vision.onnx> <imageDir>
          accuracy <vision.onnx> <imageDir>   (files named <categoryIndex>__*.ext)
          gen      <text.onnx> [output.json]
          sanity   <text-embeddings.json>      (cosine spot checks)
          synth    [outDir] [count]            (synthetic bench images)
          compare  <text-embeddings.json> <imageDir>  (fp32 vs int8 consistency + speed)
        """);

    // ── download ────────────────────────────────────────────────
    private static async Task<int> CmdDownload(string what, bool quantized)
    {
        switch (what)
        {
            case "tokenizer":
                foreach (string f in new[] { "vocab.json", "merges.txt" })
                {
                    string sha = await Downloader.DownloadAsync(TokenizerRepo, f, ModelPath(f), default);
                    Console.WriteLine($"{f}: {new FileInfo(ModelPath(f)).Length:N0} bytes  sha256={sha}");
                }
                return 0;
            case "vision":
            {
                string name = quantized ? "vision_model_quantized.onnx" : "vision_model.onnx";
                string sha = await Downloader.DownloadAsync(Repo, $"onnx/{name}", ModelPath(name), default);
                Console.WriteLine($"{name}: {new FileInfo(ModelPath(name)).Length:N0} bytes  sha256={sha}");
                return 0;
            }
            case "text":
            {
                string name = quantized ? "text_model_quantized.onnx" : "text_model.onnx";
                string sha = await Downloader.DownloadAsync(Repo, $"onnx/{name}", ModelPath(name), default);
                Console.WriteLine($"{name}: {new FileInfo(ModelPath(name)).Length:N0} bytes  sha256={sha}");
                return 0;
            }
            default:
                PrintUsage();
                return 2;
        }
    }

    // ── verify ──────────────────────────────────────────────────
    private static int CmdVerify(string modelPath)
    {
        using var session = new InferenceSession(modelPath);
        Console.WriteLine($"Inputs:");
        foreach (var input in session.InputMetadata)
            Console.WriteLine($"  {input.Key}: {Describe(input.Value)}");
        Console.WriteLine($"Outputs:");
        foreach (var output in session.OutputMetadata)
            Console.WriteLine($"  {output.Key}: {Describe(output.Value)}");

        // Run with a zero input and print actual output shapes.
        string? inputName = session.InputMetadata.Keys.FirstOrDefault();
        if (inputName == null) return 1;
        var metadata = session.InputMetadata[inputName];
        int[] dims = metadata.Dimensions.Select(d => d <= 0 ? 1 : d).ToArray();
        if (dims.Length == 4) dims = [1, 3, 224, 224];
        if (dims.Length == 2) dims = [1, 77];
        NamedOnnxValue zeroInput = metadata.ElementType == typeof(long)
            ? NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<long>(
                new long[dims.Aggregate(1, (a, b) => a * b)], dims))
            : NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(
                new float[dims.Aggregate(1, (a, b) => a * b)], dims));
        using var results = session.Run([zeroInput], session.OutputNames.ToArray());
        foreach (var result in results)
        {
            var tensor = result.AsTensor<float>();
            Console.WriteLine($"  run output '{result.Name}': dims=[{string.Join(',', tensor.Dimensions.ToArray())}]");
        }
        return 0;
    }

    private static string Describe(NodeMetadata metadata) =>
        $"dims=[{string.Join(',', metadata.Dimensions)}] type={metadata.ElementType}";

    // ── embedding helpers ───────────────────────────────────────
    private static float[] RunVision(InferenceSession session, string imagePath)
    {
        float[] pixels = ClipPreprocess.FromFile(imagePath);
        var input = NamedOnnxValue.CreateFromTensor(
            session.InputNames.First(), new DenseTensor<float>(pixels, [1, 3, ClipPreprocess.Side, ClipPreprocess.Side]));
        using var results = session.Run([input], session.OutputNames.ToArray());
        return ExtractEmbedding(results);
    }

    private static float[] RunText(InferenceSession session, ClipTokenizer tokenizer, string text)
    {
        int[] ids = tokenizer.Encode(text);
        bool useLong = session.InputMetadata.TryGetValue("input_ids", out var meta)
            && meta.ElementType == typeof(long);

        var inputs = new List<NamedOnnxValue>();
        if (session.InputNames.Contains("input_ids"))
        {
            NamedOnnxValue idsValue = useLong
                ? NamedOnnxValue.CreateFromTensor("input_ids",
                    new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, 77]))
                : NamedOnnxValue.CreateFromTensor("input_ids",
                    new DenseTensor<float>(ids.Select(i => (float)i).ToArray(), [1, 77]));
            inputs.Add(idsValue);
        }
        if (session.InputNames.Contains("attention_mask"))
        {
            var mask = new DenseTensor<long>(ids.Select(i => i == 0 ? 0L : 1L).ToArray(), [1, 77]);
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", mask));
        }

        using var results = session.Run(inputs, session.OutputNames.ToArray());
        return ExtractEmbedding(results);
    }

    /// <summary>Picks a rank-2 [*, 512] output (image_embeds/text_embeds) from results.</summary>
    private static float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (var result in results)
        {
            var tensor = result.AsTensor<float>();
            if (tensor.Dimensions.Length == 2 && tensor.Dimensions[1] == 512)
                return tensor.ToArray();
        }
        throw new InvalidDataException(
            $"No [*,512] output found. Outputs: {string.Join(',', results.Select(r => r.Name))}");
    }

    public static float Cosine(float[] a, float[] b)
    {
        return TensorPrimitives.CosineSimilarity(a, b);
    }

    // ── bench ───────────────────────────────────────────────────
    private static int CmdBench(string modelPath, string imageDir)
    {
        var files = ImageFiles(imageDir).Take(50).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("no images found"); return 1; }
        Console.WriteLine($"bench {Path.GetFileName(modelPath)} on {files.Count} images");

        using var session = new InferenceSession(modelPath);
        // Warmup.
        foreach (string f in files.Take(3)) RunVision(session, f);

        foreach (int threads in new[] { 1, 2, 4 })
        {
            var timings = new List<double>();
            var sw = new System.Diagnostics.Stopwatch();
            foreach (string f in files)
            {
                sw.Restart();
                RunVision(session, f);
                sw.Stop();
                timings.Add(sw.Elapsed.TotalMilliseconds);
            }
            var sorted = timings.OrderBy(t => t).ToList();
            double mean = timings.Average();
            double p95 = sorted[(int)(sorted.Count * 0.95)];
            Console.WriteLine($"  threads={threads}: mean={mean:F1}ms  p95={p95:F1}ms  ~{1000 / mean:F1} img/s");
        }
        return 0;
    }

    // ── accuracy ────────────────────────────────────────────────
    // File naming: <categoryIndex>__<anything>.jpg  (index into Vocab.Categories)
    private static int CmdAccuracy(string modelPath, string imageDir)
    {
        using var textSession = new InferenceSession(ModelPath("text_model.onnx"));
        var tokenizer = LoadTokenizer();
        float[][] categoryVectors = [.. Categories.Select(c => RunText(textSession, tokenizer, c.Prompt))];

        int total = 0, fp32Correct = 0;
        int[] fp32Top = new int[Categories.Count];

        using var vision = new InferenceSession(modelPath);
        foreach (string file in ImageFiles(imageDir))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            int sep = stem.IndexOf("__", StringComparison.Ordinal);
            if (sep <= 0 || !int.TryParse(stem[..sep], out int expected)) continue;
            expected = Math.Min(expected, Categories.Count - 1);
            total++;

            float[] imageEmb = RunVision(vision, file);
            int argmax = Argmax(categoryVectors.Select(v => Cosine(imageEmb, v)));
            fp32Top[argmax]++;
            if (argmax == expected) fp32Correct++;
        }

        Console.WriteLine($"total={total}  correct={fp32Correct}  accuracy={fp32Correct * 100.0 / Math.Max(1, total):F1}%");
        for (int i = 0; i < Categories.Count; i++)
            Console.WriteLine($"  {Categories[i].Name}: {fp32Top[i]}");
        return 0;
    }

    private static int Argmax(IEnumerable<float> values)
    {
        int best = 0; float bestV = float.NegativeInfinity; int i = 0;
        foreach (float v in values)
        {
            if (v > bestV) { bestV = v; best = i; }
            i++;
        }
        return best;
    }

    // ── gen text embeddings ─────────────────────────────────────
    private static int CmdGenTextEmbeddings(string textModelPath, string outputPath)
    {
        using var session = new InferenceSession(textModelPath);
        var tokenizer = LoadTokenizer();

        var doc = new Dictionary<string, object>
        {
            ["promptTemplateVersion"] = PromptTemplateVersion,
            ["categories"] = Categories.Select(c => new Dictionary<string, object>
            {
                ["name"] = c.Name,
                ["prompt"] = c.Prompt,
                ["embedding"] = RunText(session, tokenizer, c.Prompt),
            }).ToList(),
            ["tagGroups"] = TagGroups.ToDictionary(
                g => g.Key,
                g => g.Value.Select(t => new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    ["prompt"] = t.Prompt,
                    ["embedding"] = RunText(session, tokenizer, t.Prompt),
                }).ToList()),
        };

        string json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = false,
        });
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"wrote {outputPath}: {new FileInfo(outputPath).Length:N0} bytes");
        return 0;
    }

    private static ClipTokenizer LoadTokenizer() => new(
        ModelPath("vocab.json"), ModelPath("merges.txt"));

    private static IEnumerable<string> ImageFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp");

    // ── sanity: text embedding cosine spot checks ───────────────
    private static int CmdSanity(string embeddingsPath)
    {
        var doc = JsonSerializer.Deserialize<EmbeddingsDoc>(
            File.ReadAllText(embeddingsPath), CamelJson) ?? throw new InvalidDataException("parse failed");

        // Related pairs should have higher cosine than unrelated pairs.
        float[][] cats = [.. doc.Categories.Select(c => c.Embedding)];
        string[] names = [.. doc.Categories.Select(c => c.Name)];
        Console.WriteLine("Category embedding self-similarities (expect diag > off-diag):");
        for (int i = 0; i < names.Length; i++)
        {
            double bestOther = Enumerable.Range(0, names.Length)
                .Where(j => j != i).Max(j => Cosine(cats[i], cats[j]));
            Console.WriteLine($"  {names[i]}: self={Cosine(cats[i], cats[i]):F3}  best-other={bestOther:F3}");
        }

        // Prompt token sanity: known CLIP token counts.
        var tokenizer = LoadTokenizer();
        int[] ids = tokenizer.Encode("a seamless texture map pattern");
        Console.WriteLine($"'a seamless texture map pattern' → {ids.Count(i => i is not (0 or 49406 or 49407))} content tokens");
        Console.WriteLine($"  BPE parts: [{string.Join(", ", tokenizer.TokenStrings("a seamless texture map pattern"))}]");
        Console.WriteLine($"  ids: [{string.Join(",", ids.Take(30))}]");
        return 0;
    }

    // ── synth: generate deterministic synthetic bench images ────
    private static int CmdSynth(string outDir, int count)
    {
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < count; i++)
        {
            int size = 224 + (i % 6) * 120; // varied sizes 224..824
            using var image = new SixLabors.ImageSharp.Image<
                SixLabors.ImageSharp.PixelFormats.Rgb24>(size, size);
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < size; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < size; x++)
                    {
                        row[x] = new(
                            (byte)((x * 255 / size + i * 23) % 256),
                            (byte)((y * 255 / size + i * 61) % 256),
                            (byte)(((x + y) * 255 / (2 * size) + i * 127) % 256));
                    }
                }
            });
            image.Save(Path.Combine(outDir, $"synth_{i:00}.png"));
        }
        Console.WriteLine($"wrote {count} images to {outDir}");
        return 0;
    }

    // ── compare: fp32 vs int8 consistency + per-model speed ─────
    private static int CmdCompare(string embeddingsPath, string imageDir)
    {
        var doc = JsonSerializer.Deserialize<EmbeddingsDoc>(
            File.ReadAllText(embeddingsPath), CamelJson) ?? throw new InvalidDataException("parse failed");
        float[][] categoryVectors = [.. doc.Categories.Select(c => c.Embedding)];

        var files = ImageFiles(imageDir).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("no images found"); return 1; }

        using var fp32 = new InferenceSession(ModelPath("vision_model.onnx"));
        using var int8 = new InferenceSession(ModelPath("vision_model_quantized.onnx"));

        int agree = 0, total = 0;
        double minCos = 1.0, sumCos = 0;
        var fp32Times = new List<double>();
        var int8Times = new List<double>();
        var sw = new System.Diagnostics.Stopwatch();
        foreach (string file in files)
        {
            // Warmup + fp32 timing.
            float[] e32;
            sw.Restart();
            e32 = RunVision(fp32, file);
            fp32Times.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart();
            float[] e8 = RunVision(int8, file);
            int8Times.Add(sw.Elapsed.TotalMilliseconds);

            double cos = Cosine(e32, e8);
            sumCos += cos;
            minCos = Math.Min(minCos, cos);

            int cat32 = Argmax(categoryVectors.Select(v => Cosine(e32, v)));
            int cat8 = Argmax(categoryVectors.Select(v => Cosine(e8, v)));
            total++;
            if (cat32 == cat8) agree++;
        }

        Console.WriteLine($"images={total}");
        Console.WriteLine($"embedding consistency: mean cosine={sumCos / total:F4}  min={minCos:F4}");
        Console.WriteLine($"category agreement: {agree}/{total} = {agree * 100.0 / total:F1}%");
        Console.WriteLine($"fp32 speed: mean={fp32Times.Average():F1}ms  ~{1000 / fp32Times.Average():F1} img/s");
        Console.WriteLine($"int8 speed: mean={int8Times.Average():F1}ms  ~{1000 / int8Times.Average():F1} img/s  " +
                          $"speedup={fp32Times.Average() / int8Times.Average():F2}x");
        return 0;
    }

    internal sealed record EmbeddingTerm(string Name, float[] Embedding);

    internal sealed class EmbeddingsDoc
    {
        public int PromptTemplateVersion { get; set; }
        public List<EmbeddingTerm> Categories { get; set; } = [];
        public Dictionary<string, List<EmbeddingTerm>> TagGroups { get; set; } = [];
    }
}
