using QL.World.Services.Ai;

namespace QL.World.Models;

/// <summary>
/// Download progress snapshot. High-frequency updates flow through
/// IProgress&lt;T&gt; (Progress&lt;T&gt; on the ViewModel marshals to the UI
/// thread); completion/failure additionally publish a NotificationService entry.
/// </summary>
public sealed record ModelDownloadProgress(
    ModelDownloadPhase Phase,
    double Percent,
    long BytesReceived,
    long TotalBytes,
    string Message);
