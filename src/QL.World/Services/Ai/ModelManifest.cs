namespace QL.World.Services.Ai;

/// <summary>
/// Facts about the local vision model distribution, fixed by the M3-D spike
/// (tools/spike/ClipSpike/README.md): int8 failed the quality criteria, so
/// fp32 is the default tier. SHA-256 values were computed from the downloaded
/// files and are verified after download / manual import.
/// </summary>
public static class ModelManifest
{
    /// <summary>Identifier persisted alongside embeddings in image_embeddings.model_id.</summary>
    public const string ModelId = "clip-vit-b32-fp32";

    public const string ModelFileName = "vision_model.onnx";
    public const string ModelSha256 = "fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40";
    public const long ModelSizeBytes = 351_685_709;

    /// <summary>CLIP text encoder (Xenova/clip-vit-base-patch32). Bundled with the
    /// vision model in a single download per the M3-E2 plan; not used by image
    /// analysis, only by LocalAiService.EmbedTextAsync for text→image search.</summary>
    public const string TextModelFileName = "text_model.onnx";
    public const string TextModelSha256 = "3f6571f5bad13a97c469c1622e1cfc4d9aef78b79fdbfcff804ca357bfada8cc";
    public const long TextModelSizeBytes = 254_058_553;

    public const string License = "MIT";

    public const string SourceUrl =
        "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model.onnx";
    public const string MirrorUrl =
        "https://hf-mirror.com/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model.onnx";

    public const string TextModelSourceUrl =
        "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/text_model.onnx";
    public const string TextModelMirrorUrl =
        "https://hf-mirror.com/Xenova/clip-vit-base-patch32/resolve/main/onnx/text_model.onnx";

    /// <summary>Version of the prompt vocabulary baked into the text-embeddings JSON.</summary>
    public const int PromptTemplateVersion = 1;

    /// <summary>Pre-computed CLIP text embeddings shipped as an embedded resource (D-C).</summary>
    public const string TextEmbeddingsFileName = "text-embeddings-clip-vit-b32.json";
}

/// <summary>Result of CheckLocalState over the model file.</summary>
public enum LocalModelState
{
    NotDownloaded,
    Corrupted,
    Ready,
}

/// <summary>Coarse phase of a download, carried in every progress report.</summary>
public enum ModelDownloadPhase
{
    Downloading,
    Verifying,
    Completed,
}
