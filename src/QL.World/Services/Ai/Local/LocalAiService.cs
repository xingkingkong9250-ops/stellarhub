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
    private readonly Action<string> _log;
    private readonly Func<string, InferenceSession> _sessionFactory;
    private readonly Func<LocalModelState> _localStateCheck;

    private readonly object _sessionLock = new();
    private InferenceSession? _session;

    private readonly object _indexLock = new();
    private ClipTextIndex? _textIndex;
    private bool _textIndexFailed;

    public LocalAiService(AppSettingsService settings, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _modelsDirectory = Path.Combine(settings.DataRoot, "models");
        _log = log ?? (_ => { });
        _sessionFactory = CreateSession;
        _localStateCheck = DefaultLocalStateCheck;
    }

    /// <summary>Test constructor: injects a session factory and/or state check.</summary>
    internal LocalAiService(
        string modelsDirectory,
        Action<string>? log = null,
        Func<string, InferenceSession>? sessionFactory = null,
        Func<LocalModelState>? localStateCheck = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = modelsDirectory;
        _log = log ?? (_ => { });
        _sessionFactory = sessionFactory ?? CreateSession;
        _localStateCheck = localStateCheck ?? DefaultLocalStateCheck;
    }

    public string ModelFilePath => Path.Combine(_modelsDirectory, ModelManifest.ModelFileName);

    public string Mode => "Local";
    public bool SupportsEmbeddings => true;

    /// <summary>Dynamic stat — true as soon as the model file exists.</summary>
    public bool IsAvailable => File.Exists(ModelFilePath);

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_localStateCheck() == LocalModelState.Ready);

    /// <summary>
    /// Local CLIP text encoder is deferred to M3-E2 (text_model.onnx + BPE tokenizer
    /// port has BytesToUnicode traps — see project_memory). M3-E1 returns null so
    /// TopBarViewModel falls back to a notification; image→image still works in any
    /// AI mode via SemanticSearchService.SearchByImageId.
    /// </summary>
    public Task<float[]?> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        _log("[AI-LOCAL] text embed deferred to M3-E2");
        return Task.FromResult<float[]?>(null);
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
