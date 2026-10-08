using System.Collections.ObjectModel;
using System.Windows.Input;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>A1-6 消息中心弹层：未读徽标、全部已读、清空。</summary>
public sealed class NotificationListViewModel : ObservableObject
{
    private readonly NotificationService _notifications;
    private bool _isPopupOpen;

    public NotificationListViewModel(NotificationService notifications)
    {
        _notifications = notifications;
        _notifications.PropertyChanged += (_, _) => RefreshCanExecutes();
        _notifications.Changed += RefreshCanExecutes;

        TogglePopupCommand = new RelayCommand(TogglePopup);
        MarkAllReadCommand = new RelayCommand(
            () => _notifications.MarkAllRead(),
            () => _notifications.UnreadCount > 0);
        ClearAllCommand = new RelayCommand(
            () => _notifications.Clear(),
            () => _notifications.Items.Count > 0);
    }

    /// <summary>Underlying service; the popup list binds its Items directly.</summary>
    public NotificationService Notifications => _notifications;

    /// <summary>
    /// v0.7.3 修复：弹层 ListBox ItemsSource 与「暂无消息」空态绑定的是
    /// TopBar.Notifications.Items（即本 VM），此前 VM 未转发该属性，绑定
    /// 静默失败 → 列表恒空、空态文案恒显，而徽标（HasUnreadBadge）正常，
    /// 造成「有未读黄点但列表显示暂无消息」。此处转发服务的 Items。
    /// </summary>
    public ObservableCollection<NotificationEntry> Items => _notifications.Items;

    public RelayCommand TogglePopupCommand { get; }
    public RelayCommand MarkAllReadCommand { get; }
    public RelayCommand ClearAllCommand { get; }

    /// <summary>Two-way bound to the popup's IsOpen (StaysOpen=False closes on outside click).</summary>
    public bool IsPopupOpen
    {
        get => _isPopupOpen;
        set => SetProperty(ref _isPopupOpen, value);
    }

    public bool HasUnreadBadge => _notifications.UnreadCount > 0;

    public void TogglePopup()
    {
        IsPopupOpen = !IsPopupOpen;
    }

    private void RefreshCanExecutes()
    {
        OnPropertyChanged(nameof(HasUnreadBadge));
        // RelayCommand requeries on CommandManager; property changes don't trigger it.
        CommandManager.InvalidateRequerySuggested();
    }
}
