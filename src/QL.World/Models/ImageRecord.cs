namespace QL.World.Models;

/// <summary>One row of the images table.</summary>
public sealed class ImageRecord
{
    public long Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>Lower-cased file name used for stable A-Z ordering.</summary>
    public string SortableName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Format { get; set; }
    public int Rating { get; set; }
    public string Notes { get; set; } = string.Empty;

    /// <summary>Null means 未分类.</summary>
    public string? PrimaryCategory { get; set; }

    /// <summary>Reserved for M3 color analysis (JSON swatches); null until analyzed.</summary>
    public string? DominantColorsJson { get; set; }

    public bool IsMissing { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One row of the image_tags table.</summary>
public sealed record ImageTagRecord(
    long ImageId,
    string Tag,
    TagSource Source,
    double? Confidence,
    DateTime CreatedAt);

/// <summary>
/// Lightweight gallery row produced by one aggregated SQL query
/// (tags are GROUP_CONCAT'd, no N+1).
/// </summary>
public sealed class GalleryListItem
{
    public long Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string FileHash { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Format { get; init; }
    public int Rating { get; init; }
    public string? PrimaryCategory { get; init; }
    public bool IsMissing { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>Tags concatenated with 「,」 ordered by manual-first then name.</summary>
    public string TagsCsv { get; init; } = string.Empty;
}
