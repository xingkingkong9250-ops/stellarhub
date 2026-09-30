using System.Text.Json.Serialization;

namespace QL.World.Models;

/// <summary>
/// M6-E: a user-submitted image awaiting admin review. Stored as
/// <c>pending/{submit_id}.json</c> on the NAS. Approved submissions are
/// promoted into library.json and the pending file is deleted.
/// </summary>
public sealed record PendingSubmission
{
    [JsonPropertyName("submit_id")]
    public string SubmitId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("file_hash")]
    public string FileHash { get; init; } = string.Empty;

    [JsonPropertyName("file_size")]
    public long FileSize { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("primary_category")]
    public string? PrimaryCategory { get; init; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = [];

    [JsonPropertyName("submitted_by")]
    public string SubmittedBy { get; init; } = string.Empty;

    [JsonPropertyName("submitted_at")]
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>U6-B: NAS path of the uploaded original (pending/files/&lt;id&gt;/...).</summary>
    [JsonPropertyName("original_nas_path")]
    public string? OriginalNasPath { get; init; }

    /// <summary>NAS path of the submission thumbnail (pending/{submit_id}_thumb.webp).</summary>
    [JsonPropertyName("thumbnail_nas_path")]
    public string? ThumbnailNasPath { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending"; // "pending" only; approved/rejected delete the file
}
