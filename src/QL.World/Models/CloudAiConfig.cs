namespace QL.World.Models;

/// <summary>
/// Cloud AI service configuration. The API key is stored encrypted
/// (DPAPI CurrentUser) in <see cref="EncryptedApiKey"/>; never the
/// plaintext. Base URL must be HTTPS.
/// </summary>
public sealed class CloudAiConfig
{
    /// <summary>OpenAI-compatible base URL (https://...).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Chat/completion model name, e.g. "gpt-4o-mini".</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted API key (Base64). Empty when not set.</summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    /// <summary>Optional embedding model name. When empty, embeddings are skipped.</summary>
    public string EmbeddingModelName { get; set; } = string.Empty;
}
