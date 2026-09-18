namespace QL.World.Models;

/// <summary>W2 user choices for one import run.</summary>
public sealed class ImportOptions
{
    /// <summary>Selected files and/or folders.</summary>
    public IReadOnlyList<string> InputPaths { get; init; } = [];

    public bool RecurseSubfolders { get; init; } = true;

    public bool SkipHidden { get; init; } = true;

    /// <summary>Lower-cased allow-list including the dot (".jpg").</summary>
    public IReadOnlySet<string> EnabledExtensions { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif",
            ".webp", ".tif", ".tiff", ".tga",
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

/// <summary>One successfully imported row (used to enqueue thumbnails).</summary>
public sealed record ImportedItem(long Id, string FilePath, string FileHash);

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
