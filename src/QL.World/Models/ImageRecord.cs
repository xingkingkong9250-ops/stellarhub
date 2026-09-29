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

    /// <summary>
    /// M5-D (schema v6): where the original bytes live — Local (default) or
    /// Remote (NAS). Read by <see cref="QL.World.Services.DownloadQueueService"/>
    /// (M5-G) to decide between File.Copy and INasConnector download.
    /// </summary>
    public ImageSource Source { get; set; } = ImageSource.Local;

    /// <summary>
    /// M5-D (schema v6): the NAS connection id when <see cref="Source"/> is
    /// Remote; null for local rows. Read by the download queue (M5-G) to
    /// build the right INasConnector.
    /// </summary>
    public int? NasConnectionId { get; set; }
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

    /// <summary>U2-D (§4.C.2): source badge text ("本地"/"云端"/"AI") or null when no AI data.</summary>
    public string? SourceBadge { get; init; }

    /// <summary>
    /// M5-D (schema v6): where the original bytes live —
    /// <see cref="ImageSource.Local"/> (default, preserves every pre-M5 row)
    /// or <see cref="ImageSource.Remote"/> (NAS). Drives which
    /// <c>IImageSource</c> implementation <c>GalleryThumbnailProvider</c>
    /// builds for <c>ThumbnailService.Request</c> (M5-E).
    /// </summary>
    public ImageSource Source { get; init; } = ImageSource.Local;

    /// <summary>
    /// M5-D (schema v6): the NAS connection id (matching
    /// <c>AppSettings.NasConnections[i].Id</c>) when <see cref="Source"/> is
    /// <see cref="ImageSource.Remote"/>; <c>null</c> for local rows.
    /// </summary>
    public int? NasConnectionId { get; init; }
}
