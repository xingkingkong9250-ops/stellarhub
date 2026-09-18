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

    public void PublishMessage(string message) => StatusText = message;

    private void OnSettingsChanged() => OnPropertyChanged(nameof(AiStatusText));
}
