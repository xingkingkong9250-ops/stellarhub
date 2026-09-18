namespace QL.World.Models;

/// <summary>Categories of messages shown in the A1-6 notification center.</summary>
public enum NotificationKind
{
    ImportCompleted,
    ThumbnailsCompleted,
    WatcherImported,
    FilesMissing,
    LegacyImportCompleted,
    WatcherDirectoryError,
    AiBatchProgress,
    AiBatchCompleted,
}

/// <summary>A single session-scoped notification entry.</summary>
public sealed class NotificationEntry
{
    private static long s_nextId = 1;

    public long Id { get; }
    public DateTime CreatedAt { get; }

    /// <summary>Local-time display string for the notification list.</summary>
    public string CreatedAtText => CreatedAt.ToLocalTime().ToString("HH:mm:ss");
    public NotificationKind Kind { get; }
    public string Title { get; }
    public string Message { get; }
    public bool IsUnread { get; set; }
    public long? RelatedImageId { get; }
    public string? RelatedFilePath { get; }

    public NotificationEntry(
        NotificationKind kind,
        string title,
        string message,
        long? relatedImageId = null,
        string? relatedFilePath = null)
    {
        Id = Interlocked.Increment(ref s_nextId);
        CreatedAt = DateTime.UtcNow;
        Kind = kind;
        Title = title;
        Message = message;
        IsUnread = true;
        RelatedImageId = relatedImageId;
        RelatedFilePath = relatedFilePath;
    }
}
