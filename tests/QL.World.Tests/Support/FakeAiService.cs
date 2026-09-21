using QL.World.Models.Ai;
using QL.World.Services.Ai;

namespace QL.World.Tests.Support;

/// <summary>
/// Configurable mock IAiService for tests. Extracted from AiAnalysisServiceTests
/// so SemanticSearchServiceTests and TopBarViewModelTests can share it.
/// </summary>
public sealed class FakeAiService : IAiService
{
    public string Mode => "Fake";
    public bool IsAvailable { get; set; } = true;
    public bool SupportsEmbeddings { get; set; } = true;

    public Func<string, AiAnalysisResult>? AnalyzeFunc { get; set; }

    /// <summary>
    /// Returns a canned text embedding for EmbedTextAsync. Null (default) means
    /// the fake backend cannot produce text embeddings — SemanticSearchService
    /// surfaces this as "semantic search unavailable".
    /// </summary>
    public Func<string, float[]?>? EmbedTextFunc { get; set; }

    public Task<AiAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken ct)
    {
        AiAnalysisResult result = AnalyzeFunc?.Invoke(imagePath)
            ?? new AiAnalysisResult { ErrorMessage = "no func" };
        return Task.FromResult(result);
    }

    public Task<float[]?> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        float[]? result = EmbedTextFunc?.Invoke(text);
        return Task.FromResult(result);
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
}
