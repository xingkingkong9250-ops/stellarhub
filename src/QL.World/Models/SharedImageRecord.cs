using System.Text.Json.Serialization;

namespace QL.World.Models;

/// <summary>
/// M6-D: a single image in the NAS-shared library (stored in library.json).
/// Field-aligned with the local <c>ImageRecord</c> so the gallery can render
/// both sources uniformly. No SQLite — the whole list is loaded into memory.
/// </summary>
public sealed record SharedImageRecord
{
    /// <summary>SHA-256 of the original file bytes; doubles as the stable id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

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

    [JsonPropertyName("rating")]
    public int Rating { get; init; }

    [JsonPropertyName("primary_category")]
    public string? PrimaryCategory { get; init; }

    [JsonPropertyName("dominant_colors")]
    public List<string> DominantColors { get; init; } = [];

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = [];

    /// <summary>NAS path of the original file (for download / open).</summary>
    [JsonPropertyName("source_nas_path")]
    public string? SourceNasPath { get; init; }

    /// <summary>
    /// M8-A: 该条数据所属的 NAS 连接 ID（spec.Id）。持久化于 library.json 便于
    /// 跨重启保留来源；运行时由 <c>AggregatedSharedLibraryService</c> 按 spec.Id
    /// 覆盖（防 spec 删除后 stale）。null 表示主库（向后兼容旧 library.json）。
    /// </summary>
    [JsonPropertyName("source_nas_id")]
    public int? SourceNasId { get; init; }

    [JsonPropertyName("submitted_by")]
    public string SubmittedBy { get; init; } = string.Empty;

    [JsonPropertyName("approved_at")]
    public DateTimeOffset ApprovedAt { get; init; }

    [JsonPropertyName("approved_by")]
    public string ApprovedBy { get; init; } = string.Empty;
}
