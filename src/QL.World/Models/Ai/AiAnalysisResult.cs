namespace QL.World.Models.Ai;

/// <summary>A single AI-generated tag with its confidence score.</summary>
public sealed record AiTag(string Tag, double Confidence);

/// <summary>
/// Result of analyzing one image through an <see cref="QL.World.Services.Ai.IAiService"/>.
/// On failure, <see cref="ErrorMessage"/> is set and other fields are default.
/// </summary>
public sealed class AiAnalysisResult
{
    /// <summary>Primary category (10-class system). Null when unavailable.</summary>
    public string? PrimaryCategory { get; init; }

    /// <summary>AI-generated tags with confidence (0–1).</summary>
    public IReadOnlyList<AiTag> Tags { get; init; } = [];

    /// <summary>Image embedding vector (float). Null when embeddings are unsupported.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Model identifier that produced this result (for embedding header).</summary>
    public string? ModelId { get; init; }

    /// <summary>Set when the analysis failed; null/empty on success.</summary>
    public string? ErrorMessage { get; init; }

    public bool IsError => !string.IsNullOrEmpty(ErrorMessage);
}
