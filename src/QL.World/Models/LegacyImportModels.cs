namespace QL.World.Models;

/// <summary>Streaming progress for the legacy Python library import (W3/A5).</summary>
public sealed class LegacyImportProgress
{
    public int ScannedTotal { get; set; }
    public int Processed { get; set; }
    public int Added { get; set; }
    public int Skipped { get; set; }
    public string? CurrentFile { get; set; }
}

/// <summary>Final result of one legacy Python library import run.</summary>
public sealed class LegacyImportReport
{
    public int ScannedTotal { get; set; }
    public int AddedCount { get; set; }
    public int SkippedCount { get; set; }
    public int TagCount { get; set; }
    public int CollectionCount { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>Non-null means the import could not start or aborted early.</summary>
    public string? ErrorMessage { get; set; }

    public List<string> LogLines { get; } = [];
}
