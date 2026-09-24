using System.Windows.Threading;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>C1 status bar.</summary>
public sealed class StatusBarViewModel : ObservableObject
{
    private readonly ThumbnailService _thumbnails;
    private readonly AppSettingsService _settings;
    private readonly DispatcherTimer _timer;
    private string _statusText = "就绪";
    private string _shortcutHint = "Ctrl+I 导入";
    private int _thumbnailPending;
    private int _totalCount;
    private int _filteredCount;

    public StatusBarViewModel(ThumbnailService thumbnails, AppSettingsService settings)
    {
        _thumbnails = thumbnails;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            ThumbnailPending = _thumbnails.PendingCount;
            StatusText = _thumbnails.PendingCount > 0
                ? $"后台生成缩略图：{_thumbnails.PendingCount}"
                : "就绪";
        };
        _timer.Start();
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public int ThumbnailPending
    {
        get => _thumbnailPending;
        private set => SetProperty(ref _thumbnailPending, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        set => SetProperty(ref _totalCount, value);
    }

    public int FilteredCount
    {
        get => _filteredCount;
        set => SetProperty(ref _filteredCount, value);
    }

    /// <summary>C1-R 上下文相关快捷键提示。由 ShellViewModel 在选中态/刷新时更新。</summary>
    public string ShortcutHint
    {
        get => _shortcutHint;
        private set => SetProperty(ref _shortcutHint, value);
    }

    public string AiStatusText
    {
        get
        {
            return _settings.AiMode switch
            {
                AiMode.Cloud => "AI：云端",
                AiMode.Local => "AI：本地",
                _ => "AI：关闭",
            };
        }
    }

    public void PublishCounts(int total, int filtered)
    {
        TotalCount = total;
        FilteredCount = filtered;
    }

    /// <summary>
    /// C1-R 快捷键提示按上下文切换。优先级（高→低）：
    /// 空库 → 多选 → 单选 → 无选中。
    /// 仅显示 InputBindings 已绑定的快捷键，不引入新的。
    /// </summary>
    public void UpdateShortcutHint(int selectedCount, int totalCount)
    {
        string hint = (totalCount, selectedCount) switch
        {
            (0, _) => "Ctrl+I 导入",
            (_, > 1) => "Ctrl+1…5 批量评分 · Del 批量移除",
            (_, 1) => "Ctrl+1…5 评分 · Del 移除 · Ctrl+F 搜索",
            _ => "Ctrl+I 导入 · Ctrl+F 搜索 · F5 刷新",
        };
        ShortcutHint = hint;
    }

    public void PublishMessage(string message) => StatusText = message;

    private void OnSettingsChanged() => OnPropertyChanged(nameof(AiStatusText));
}
