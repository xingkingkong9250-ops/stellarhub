using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// Session-scoped message center (A1-6). Not persisted to disk.
/// Background tasks publish completion/missing-file notices here.
/// </summary>
public sealed class NotificationService : ObservableObject
{
    private int _unreadCount;

    public ObservableCollection<NotificationEntry> Items { get; } = [];

    public int UnreadCount
    {
        get => _unreadCount;
        private set => SetProperty(ref _unreadCount, value);
    }

    /// <summary>Raised on any mutation (publish, mark-read, clear).</summary>
    public event Action? Changed;

    public void Publish(
        NotificationKind kind,
        string title,
        string message,
        long? relatedImageId = null,
        string? relatedFilePath = null)
    {
        var entry = new NotificationEntry(kind, title, message,
            relatedImageId, relatedFilePath);
        Items.Insert(0, entry);
        UnreadCount++;
        Changed?.Invoke();
    }

    /// <summary>Aggregates multiple file paths into a single notification entry.</summary>
    public void PublishBatch(
        NotificationKind kind,
        string title,
        IReadOnlyList<string> filePaths)
    {
        string message = filePaths.Count switch
        {
            1 => filePaths[0],
            _ => $"{filePaths.Count} 个文件\n{string.Join('\n', filePaths.Take(5))}{(filePaths.Count > 5 ? "\n…" : "")}",
        };
        Publish(kind, title, message, relatedFilePath: filePaths.Count == 1 ? filePaths[0] : null);
    }

    public void MarkRead(long id)
    {
        foreach (NotificationEntry entry in Items)
        {
            if (entry.Id == id && entry.IsUnread)
            {
                entry.IsUnread = false;
                UnreadCount = Math.Max(0, UnreadCount - 1);
                Changed?.Invoke();
                return;
            }
        }
    }

    public void MarkAllRead()
    {
        bool anyChanged = false;
        foreach (NotificationEntry entry in Items)
        {
            if (entry.IsUnread)
            {
                entry.IsUnread = false;
                anyChanged = true;
            }
        }
        if (anyChanged)
        {
            UnreadCount = 0;
            Changed?.Invoke();
        }
    }

    public void Clear()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        UnreadCount = 0;
        Changed?.Invoke();
    }

    public void ClearByKind(NotificationKind kind)
    {
        var toRemove = Items.Where(e => e.Kind == kind).ToList();
        if (toRemove.Count == 0) return;
        int removedUnread = toRemove.Count(e => e.IsUnread);
        foreach (NotificationEntry entry in toRemove)
            Items.Remove(entry);
        if (removedUnread > 0)
            UnreadCount = Math.Max(0, UnreadCount - removedUnread);
        Changed?.Invoke();
    }
}
