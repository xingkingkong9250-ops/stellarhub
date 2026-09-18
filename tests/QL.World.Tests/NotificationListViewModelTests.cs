using QL.World.Models;
using QL.World.Services;
using QL.World.ViewModels;

namespace QL.World.Tests;

public sealed class NotificationListViewModelTests
{
    [Fact]
    public void HasUnreadBadge_tracks_unread_count()
    {
        var service = new NotificationService();
        var vm = new NotificationListViewModel(service);

        Assert.False(vm.HasUnreadBadge);

        service.Publish(NotificationKind.ImportCompleted, "导入完成", "已导入 3 张");
        Assert.True(vm.HasUnreadBadge);

        service.MarkAllRead();
        Assert.False(vm.HasUnreadBadge);
    }

    [Fact]
    public void TogglePopup_flips_IsPopupOpen()
    {
        var vm = new NotificationListViewModel(new NotificationService());
        Assert.False(vm.IsPopupOpen);

        vm.TogglePopup();
        Assert.True(vm.IsPopupOpen);

        vm.TogglePopup();
        Assert.False(vm.IsPopupOpen);
    }

    [Fact]
    public void MarkAllRead_command_only_enabled_with_unread()
    {
        var service = new NotificationService();
        var vm = new NotificationListViewModel(service);
        Assert.False(vm.MarkAllReadCommand.CanExecute(null));

        service.Publish(NotificationKind.FilesMissing, "文件缺失", "x.png");
        Assert.True(vm.MarkAllReadCommand.CanExecute(null));

        vm.MarkAllReadCommand.Execute(null);
        Assert.Equal(0, service.UnreadCount);
        Assert.False(vm.MarkAllReadCommand.CanExecute(null));
        Assert.Single(service.Items);
    }

    [Fact]
    public void ClearAll_command_only_enabled_with_items()
    {
        var service = new NotificationService();
        var vm = new NotificationListViewModel(service);
        Assert.False(vm.ClearAllCommand.CanExecute(null));

        service.Publish(NotificationKind.WatcherImported, "监控导入", "y.png");
        Assert.True(vm.ClearAllCommand.CanExecute(null));

        vm.ClearAllCommand.Execute(null);
        Assert.Empty(service.Items);
        Assert.False(vm.ClearAllCommand.CanExecute(null));
    }

    [Fact]
    public void Popup_list_binds_service_items_directly()
    {
        var service = new NotificationService();
        var vm = new NotificationListViewModel(service);
        Assert.Same(service.Items, vm.Notifications.Items);
    }
}
