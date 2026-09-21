using QL.World.Models.Ai;

namespace QL.World.Services.Ai;

/// <summary>
/// No-op backend used when AI is disabled. AnalyzeImageAsync returns an
/// error result and IsAvailable is false, so the UI greys out AI entry points.
/// </summary>
public sealed class DisabledAiService : IAiService
{
    public string Mode => "Disabled";
    public bool IsAvailable => false;
    public bool SupportsEmbeddings => false;

    public Task<AiAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        return Task.FromResult(new AiAnalysisResult
        {
            ErrorMessage = "AI 分析未启用。请在设置中选择本地或云端模式。",
        });
    }

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<float[]?> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.FromResult<float[]?>(null);
    }
}
