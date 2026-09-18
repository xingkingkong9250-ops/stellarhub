using QL.World.Models.Ai;

namespace QL.World.Services.Ai;

/// <summary>
/// Abstraction over the AI analysis backend (disabled / local ONNX / cloud).
/// All methods must never throw network/runtime failures — failures are
/// surfaced via <see cref="AiAnalysisResult.ErrorMessage"/> or a false return.
/// </summary>
public interface IAiService
{
    /// <summary>Mode identifier matching <see cref="QL.World.Models.AiMode"/>.</summary>
    string Mode { get; }

    /// <summary>True when the service is configured and ready to analyze.</summary>
    bool IsAvailable { get; }

    /// <summary>True when this backend can produce embedding vectors.</summary>
    bool SupportsEmbeddings { get; }

    /// <summary>
    /// Analyzes a single image file. Never throws; returns an
    /// <see cref="AiAnalysisResult"/> with <see cref="AiAnalysisResult.ErrorMessage"/>
    /// set on failure.
    /// </summary>
    Task<AiAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken cancellationToken);

    /// <summary>
    /// Tests connectivity/authentication against the configured backend.
    /// Returns false on any failure (network, auth, misconfiguration).
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken);
}
