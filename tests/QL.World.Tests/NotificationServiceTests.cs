using QL.World.Models;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public void Publish_appends_unread_entry_and_increments_count()
    {
        var svc = new NotificationService();
        svc.Publish(NotificationKind.ImportCompleted, "导入完成", "新增 5 张图片");

        Assert.Single(svc.Items);
        Assert.Equal(1, svc.UnreadCount);
        Assert.True(svc.Items[0].IsUnread);
        Assert.Equal("导入完成", svc.Items[0].Title);
    }

    [Fact]
    public void MarkAllRead_zeroes_unread_count_but_keeps_entries()
    {
        var svc = new NotificationService();
        svc.Publish(NotificationKind.ImportCompleted, "A", "a");
        svc.Publish(NotificationKind.FilesMissing, "B", "b");
        Assert.Equal(2, svc.UnreadCount);

        svc.MarkAllRead();
        Assert.Equal(0, svc.UnreadCount);
        Assert.Equal(2, svc.Items.Count);
        Assert.All(svc.Items, e => Assert.False(e.IsUnread));
    }

    [Fact]
    public void Clear_empties_collection_and_resets_count()
    {
        var svc = new NotificationService();
        svc.Publish(NotificationKind.ImportCompleted, "A", "a");
        svc.Publish(NotificationKind.FilesMissing, "B", "b");

        svc.Clear();
        Assert.Empty(svc.Items);
        Assert.Equal(0, svc.UnreadCount);
    }

    [Fact]
    public void PublishBatch_creates_single_aggregated_entry()
    {
        var svc = new NotificationService();
        svc.PublishBatch(NotificationKind.FilesMissing, "文件缺失",
            ["C:\\a.png", "C:\\b.png", "C:\\c.png"]);

        Assert.Single(svc.Items);
        Assert.Equal(1, svc.UnreadCount);
        Assert.Contains("3 个文件", svc.Items[0].Message);
    }

    [Fact]
    public void ClearByKind_removes_only_matching_kind()
    {
        var svc = new NotificationService();
        svc.Publish(NotificationKind.ImportCompleted, "A", "a");
        svc.Publish(NotificationKind.FilesMissing, "B", "b");
        svc.Publish(NotificationKind.FilesMissing, "C", "c");

        svc.ClearByKind(NotificationKind.FilesMissing);
        Assert.Single(svc.Items);
        Assert.Equal(NotificationKind.ImportCompleted, svc.Items[0].Kind);
        Assert.Equal(1, svc.UnreadCount);
    }

    [Fact]
    public void MarkRead_decrements_unread_count()
    {
        var svc = new NotificationService();
        svc.Publish(NotificationKind.ImportCompleted, "A", "a");
        long entryId = svc.Items[0].Id;

        svc.MarkRead(entryId);
        Assert.Equal(0, svc.UnreadCount);
        Assert.False(svc.Items[0].IsUnread);
    }
}
