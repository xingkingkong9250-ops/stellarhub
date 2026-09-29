namespace QL.World.Models;

/// <summary>
/// One row of the <c>download_queue</c> table (M5-G). Persisted so a
/// download interrupted by app restart / network drop can resume from the
/// last committed byte. Status transitions follow the state machine
/// <c>pending → downloading → completed | failed | cancelled</c>.
/// </summary>
public sealed class DownloadQueueItem
{
    public long Id { get; set; }

    public long AssetId { get; set; }

    /// <summary>Display file name (from the linked images row).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Absolute local destination path.</summary>
    public string LocalTarget { get; set; } = string.Empty;

    /// <summary>
    /// One of: pending / downloading / completed / failed / cancelled.
    /// Stored as TEXT in SQLite.
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>0..1 fraction of bytes written (set by the worker).</summary>
    public double Progress { get; set; }

    /// <summary>Last failure message; null when no error.</summary>
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// M5-G routing info: copied from the images row at enqueue time so the
    /// worker doesn't have to re-query when resuming. Null for local rows.
    /// </summary>
    public int? NasConnectionId { get; set; }

    /// <summary>Local or Remote — drives File.Copy vs INasConnector path.</summary>
    public ImageSource Source { get; set; } = ImageSource.Local;
}
