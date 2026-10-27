using System.Text.Json.Serialization;

namespace QL.World.Models;

/// <summary>
/// PEND-004 (0.18.0): a user-curated collection inside the NAS shared library
/// (stored in <c>collections.json</c>). Image membership references
/// <see cref="SharedImageRecord.Id"/> (file hash), so the same collection works
/// across the aggregated primary + secondary library view.
/// </summary>
public sealed record SharedCollection
{
    /// <summary>Stable opaque id (generated on creation; never reused).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Shared image hashes (<see cref="SharedImageRecord.Id"/>), in display order.</summary>
    [JsonPropertyName("image_ids")]
    public List<string> ImageIds { get; init; } = [];

    [JsonPropertyName("created_by")]
    public string CreatedBy { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}
