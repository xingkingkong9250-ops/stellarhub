using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using QL.World.Models.Ai;

namespace QL.World.Services.Ai.Local;

/// <summary>
/// Local ONNX inference backend (CLIP ViT-B/32, fp32 per the M3-D spike).
/// The InferenceSession is created lazily on first inference and all session
/// access is serialized. Availability is a dynamic file stat so a model that
/// finishes downloading mid-session is picked up immediately. Every failure
/// path returns an error result — nothing throws out of the service.
/// </summary>
public sealed class LocalAiService : IAiService
{
    private readonly string _modelsDirectory;
    private readonly string? _modelFileOverride;
    private readonly Action<string> _log;
    private readonly Func<string, InferenceSession> _sessionFactory;
    private readonly Func<string, InferenceSession>? _textSessionFactory;
    private readonly Func<LocalModelState> _localStateCheck;

    private readonly object _sessionLock = new();
    private InferenceSession? _session;

    private readonly object _textSessionLock = new();
    private InferenceSession? _textSession;

    private readonly object _tokenizerLock = new();
    private ClipTokenizer? _tokenizer;
    private bool _tokenizerFailed;

    private readonly object _indexLock = new();
    private ClipTextIndex? _textIndex;
    private bool _textIndexFailed;

    public LocalAiService(AppSettingsService settings, Action<string>? log = null, string? modelPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (modelPath is null)
        {
            // 优先从打包位置（exe 同目录 models\）加载，无则回落到 dataRoot\models\。
            _modelsDirectory = ResolveModelsDirectory(settings.DataRoot);
            _modelFileOverride = null;
        }
        else
        {
            // v0.12.0: 用户自加本地模型——直接用该文件路径。
            _modelFileOverride = modelPath;
            _modelsDirectory = Path.GetDirectoryName(modelPath) ?? modelPath;
        }
        _log = log ?? (_ => { });
        _sessionFactory = CreateSession;
        _textSessionFactory = null; // production: text session uses the same CreateSession
        _localStateCheck = DefaultLocalStateCheck;
    }

    /// <summary>
    /// 优先返回打包位置（AppContext.BaseDirectory\models）——若该目录存在
    /// vision_model.onnx 则视为已打包，直接使用；否则回落到 dataRoot\models。
    /// </summary>
    private static string ResolveModelsDirectory(string dataRoot)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "models");
        if (File.Exists(Path.Combine(bundled, ModelManifest.ModelFileName)))
            return bundled;
        return Path.Combine(dataRoot, "models");
    }

    /// <summary>Test constructor: injects session factories and/or a state check.</summary>
    internal LocalAiService(
        string modelsDirectory,
        Action<string>? log = null,
        Func<string, InferenceSession>? sessionFactory = null,
        Func<LocalModelState>? localStateCheck = null,
        Func<string, InferenceSession>? textSessionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = modelsDirectory;
        _modelFileOverride = null;
        _log = log ?? (_ => { });
        _sessionFactory = sessionFactory ?? CreateSession;
        _textSessionFactory = textSessionFactory; // null falls back to _sessionFactory
        _localStateCheck = localStateCheck ?? DefaultLocalStateCheck;
    }

    public string ModelFilePath =>
        _modelFileOverride ?? Path.Combine(_modelsDirectory, ModelManifest.ModelFileName);
    public string TextModelFilePath => Path.Combine(_modelsDirectory, ModelManifest.TextModelFileName);

    public string Mode => "Local";
    public bool SupportsEmbeddings => true;

    /// <summary>
    /// Dynamic stat — true as soon as the vision model file exists. Image
    /// analysis and image→image search work whenever vision is present;
    /// text→image search additionally needs text_model.onnx (checked per-call
    /// in EmbedTextAsync).
    /// </summary>
    public bool IsAvailable => File.Exists(ModelFilePath);

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_localStateCheck() == LocalModelState.Ready);

    /// <summary>
    /// Tokenizes the input via the local BPE tokenizer and runs text_model.onnx
    /// to produce a 512-dim CLIP text embedding. Returns null (no throw) when:
    /// - the text model file is missing (user hasn't downloaded the bundle yet)
    /// - tokenizer resource extraction / parsing fails (sets a sticky flag)
    /// - ONNX session creation or inference fails
    /// Caller surfaces a notification and falls back to Tag-mode search.
    /// </summary>
    public async Task<float[]?> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            if (!File.Exists(TextModelFilePath))
            {
                _log("[AI-LOCAL] text model missing; EmbedTextAsync returns null");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ClipTokenizer? tokenizer = GetTokenizer();
            if (tokenizer is null)
                return null;

            int[] ids = await Task.Run(() => tokenizer.Encode(text), cancellationToken);
            InferenceSession textSession = GetTextSession();

            long[] idsLong = ids.Select(i => (long)i).ToArray();
            var input = NamedOnnxValue.CreateFromTensor(
                "input_ids", new DenseTensor<long>(idsLong, [1, 77]));

            float[] embedding = await Task.Run(() =>
            {
                lock (_textSessionLock)
                {
                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                        textSession.Run([input], textSession.OutputNames.ToArray());
                    return ExtractEmbedding(results);
                }
            }, cancellationToken);

            return embedding;
        }
        catch (Exception ex)
        {
            _log($"[AI-LOCAL] EmbedTextAsync failed: {ex.Message}");
            return null;
        }
    }

    public async Task<AiAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        try
        {
            if (!File.Exists(ModelFilePath))
                return new AiAnalysisResult
                {
                    ErrorMessage = "本地模型未下载，请先在设置中下载或导入模型。",
                };

            cancellationToken.ThrowIfCancellationRequested();
            float[] tensor = await Task.Run(() => ClipPreprocessor.FromFile(imagePath), cancellationToken);
            float[] embedding = await Task.Run(() => RunInference(tensor), cancellationToken);
            ClipRankResult rank = RankAgainstVocabulary(embedding);

            return new AiAnalysisResult
            {
                PrimaryCategory = rank.PrimaryCategory,
                Tags = rank.Tags,
                Embedding = embedding,
                ModelId = ModelManifest.ModelId,
            };
        }
        catch (Exception ex)
        {
            return new AiAnalysisResult { ErrorMessage = $"本地分析失败：{ex.Message}" };
        }
    }

    private float[] RunInference(float[] tensor)
    {
        InferenceSession session = GetSession();
        var input = NamedOnnxValue.CreateFromTensor(
            session.InputNames.First(),
            new DenseTensor<float>(tensor, [1, 3, ClipPreprocessor.Side, ClipPreprocessor.Side]));

        lock (_sessionLock)
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                session.Run([input], session.OutputNames.ToArray());
            return ExtractEmbedding(results);
        }
    }

    private InferenceSession GetSession()
    {
        if (_session is not null) return _session;
        lock (_sessionLock)
        {
            if (_session is null)
            {
                _log($"[AI-LOCAL] creating InferenceSession: {ModelFilePath}");
                _session = _sessionFactory(ModelFilePath);
            }
            return _session;
        }
    }

    private InferenceSession GetTextSession()
    {
        if (_textSession is not null) return _textSession;
        lock (_textSessionLock)
        {
            if (_textSession is null)
            {
                _log($"[AI-LOCAL] creating text InferenceSession: {TextModelFilePath}");
                Func<string, InferenceSession> factory = _textSessionFactory ?? _sessionFactory;
                _textSession = factory(TextModelFilePath);
            }
            return _textSession;
        }
    }

    private ClipTokenizer? GetTokenizer()
    {
        if (_tokenizer is not null || _tokenizerFailed) return _tokenizer;
        lock (_tokenizerLock)
        {
            if (_tokenizer is not null || _tokenizerFailed) return _tokenizer;
            try
            {
                (string vocabPath, string mergesPath) = ClipTokenizer.EnsureDistributed(_modelsDirectory);
                _tokenizer = new ClipTokenizer(vocabPath, mergesPath);
            }
            catch (Exception ex)
            {
                _tokenizerFailed = true;
                _log($"[AI-LOCAL] failed to load ClipTokenizer: {ex.Message}");
            }
            return _tokenizer;
        }
    }

    private static InferenceSession CreateSession(string modelPath)
    {
        var options = new SessionOptions
        {
            // Half the logical cores: keep the UI responsive during batch runs.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        return new InferenceSession(modelPath, options);
    }

    private static float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        foreach (DisposableNamedOnnxValue result in results)
        {
            if (result.Value is Tensor<float> tensor &&
                tensor.Dimensions.Length == 2 &&
                tensor.Dimensions[1] == 512)
                return tensor.ToArray();
        }
        throw new InvalidDataException("模型输出中没有 [*,512] 的 embedding 向量。");
    }

    private ClipRankResult RankAgainstVocabulary(float[] embedding)
    {
        try
        {
            ClipTextIndex? index = GetTextIndex();
            return index is null ? new ClipRankResult(null, []) : index.Rank(embedding);
        }
        catch (Exception ex)
        {
            _log($"[AI-LOCAL] text index unavailable, skipping tags: {ex.Message}");
            return new ClipRankResult(null, []);
        }
    }

    private ClipTextIndex? GetTextIndex()
    {
        if (_textIndex is not null || _textIndexFailed) return _textIndex;
        lock (_indexLock)
        {
            if (_textIndex is not null || _textIndexFailed) return _textIndex;
            try
            {
                string path = ClipTextIndex.EnsureDistributed(_modelsDirectory);
                _textIndex = ClipTextIndex.LoadFromFile(path);
            }
            catch (Exception ex)
            {
                _textIndexFailed = true;
                _log($"[AI-LOCAL] failed to load text embeddings: {ex.Message}");
            }
            return _textIndex;
        }
    }

    /// <summary>
    /// Cheap existence+size check for TestConnection. The full SHA-256
    /// verification lives in ModelDownloadService (settings UI); here a size
    /// mismatch is reported as Corrupted and real corruption is surfaced by
    /// session creation failing during inference.
    /// </summary>
    private LocalModelState DefaultLocalStateCheck()
    {
        if (!File.Exists(ModelFilePath))
            return LocalModelState.NotDownloaded;
        return new FileInfo(ModelFilePath).Length == ModelManifest.ModelSizeBytes
            ? LocalModelState.Ready
            : LocalModelState.Corrupted;
    }
}
