namespace QL.World.Models;

/// <summary>
/// AI backend kind. A backend is always either <see cref="Local"/> (ONNX)
/// or <see cref="Cloud"/> (OpenAI-compatible API). "Disabled" is represented
/// by a null <see cref="AppSettings.ActiveAiBackendId"/> rather than a kind
/// here, so there is no Disabled member.
/// </summary>
public enum AiBackendKind
{
    /// <summary>Local ONNX model (bundled or user-added).</summary>
    Local = 0,

    /// <summary>OpenAI-compatible cloud API over HTTPS.</summary>
    Cloud = 1,
}

/// <summary>
/// Persisted AI backend definition stored in <see cref="AppSettings.AiBackends"/>.
/// Mirrors the <c>NasConnectionSpec</c> pattern: stable numeric <see cref="Id"/>,
/// DPAPI-encrypted secrets (<see cref="EncryptedApiKey"/>), monotonic id
/// assignment via <c>AddAiBackend</c>, and <see cref="CreatedAt"/>/<see cref="UpdatedAt"/>
/// timestamps. The built-in local backend has <see cref="IsBundled"/>=true and
/// cannot be deleted.
/// </summary>
public sealed class AiBackendConfig
{
    /// <summary>
    /// Stable numeric id; assigned monotonically by <c>AppSettingsService.AddAiBackend</c>
    /// (max existing id + 1, or 1 for the first; deleted ids are never reused).
    /// </summary>
    public int Id { get; set; }

    /// <summary>User-facing display name, e.g. "内置模型" or "我的 GPT-4o".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Backend kind: Local (ONNX) or Cloud (OpenAI-compatible).</summary>
    public AiBackendKind Kind { get; set; } = AiBackendKind.Local;

    // ─── Cloud fields (populated when Kind == Cloud) ──────────────────────

    /// <summary>OpenAI-compatible base URL (https://...). Empty for local backends.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Chat/completion model name, e.g. "gpt-4o-mini".</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted API key (Base64). Plaintext is never stored. Empty when not set.</summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    /// <summary>Optional embedding model name. Empty = skip embeddings.</summary>
    public string EmbeddingModelName { get; set; } = string.Empty;

    // ─── Local fields (populated when Kind == Local) ───────────────────────

    /// <summary>
    /// Full path to the ONNX model file. Null = use the bundled model (shipped
    /// in the models\ directory next to the executable). Non-null = user-added
    /// custom model file path.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// True for the built-in bundled backend. Cannot be deleted
    /// (<c>DeleteAiBackend</c> refuses). Exactly one bundled backend exists
    /// per installation.
    /// </summary>
    public bool IsBundled { get; set; }

    /// <summary>Creation timestamp (UTC). Assigned by <c>AddAiBackend</c>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last modification timestamp (UTC). Stamped by add/update.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
