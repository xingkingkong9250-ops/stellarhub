namespace QL.World.Models;

/// <summary>W2 user choices for one import run.</summary>
public sealed class ImportOptions
{
    /// <summary>Selected files and/or folders.</summary>
    public IReadOnlyList<string> InputPaths { get; init; } = [];

    public bool RecurseSubfolders { get; init; } = true;

    public bool SkipHidden { get; init; } = true;

    /// <summary>
    /// Local file system (default) or NAS via <see cref="ImageSource.Remote"/>.
    /// Drives which <see cref="QL.World.Services.Nas.IFileSystemAccessor"/>
    /// the importer resolves, and the value written to <c>images.source</c>.
    /// </summary>
    public ImageSource Source { get; init; } = ImageSource.Local;

    /// <summary>
    /// When <see cref="Source"/> is <see cref="ImageSource.Remote"/>, the id of
    /// the NAS connection (in <see cref="AppSettingsService.NasConnections"/>)
    /// the bytes live behind. Null for local rows. Written to
    /// <c>images.nas_connection_id</c>.
    /// </summary>
    public int? NasConnectionId { get; init; }

    /// <summary>
    /// M11: 一级分类名，决定分类层硬链接目录 library\<分类>\。
    /// null/空 → 归入「未分类」。由 W2 导入对话框提供（未选择即未分类）。
    /// </summary>
    public string? PrimaryCategory { get; init; }

    /// <summary>Lower-cased allow-list including the dot (".jpg").</summary>
    public IReadOnlySet<string> EnabledExtensions { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif",
            ".webp", ".tif", ".tiff", ".tga",
            ".exr", ".hdr", ".psd",
        };
}

/// <summary>Live progress surfaced on W2 and C1.</summary>
public sealed class ImportProgress
{
    public int ScannedTotal { get; set; }
    public int Processed { get; set; }
    public int Added { get; set; }
    public int Skipped { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}

/// <summary>
/// One successfully imported row (used to enqueue thumbnails).
/// M11: <paramref name="FilePath"/> is the category hardlink in managed
/// mode (legacy mode: source path); <paramref name="OriginalPath"/> is
/// always the external/source path the bytes came from.
/// </summary>
public sealed record ImportedItem(long Id, string FilePath, string FileHash, string OriginalPath = "");

/// <summary>Outcome of an import run.</summary>
public sealed class ImportReport
{
    public int ScannedTotal { get; set; }
    public int AddedCount => Added.Count;
    public int SkippedCount { get; set; }
    public bool Cancelled { get; set; }
    public List<ImportedItem> Added { get; } = [];
    public List<string> LogLines { get; } = [];
}
