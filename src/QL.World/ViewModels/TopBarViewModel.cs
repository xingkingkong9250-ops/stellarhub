using System.Windows.Threading;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;

namespace QL.World.ViewModels;

/// <summary>A1 top bar (import / AI / view mode / search / notifications / settings).</summary>
public sealed class TopBarViewModel : ObservableObject
{
    private const int SEARCH_DEBOUNCE_MS = 300;

    private readonly GalleryViewModel _gallery;
    private readonly IAiService _ai;
    private readonly AiAnalysisService _aiAnalysis;
    private readonly DispatcherTimer _searchTimer;
    private string _searchText = string.Empty;
    private SearchMode _searchMode = SearchMode.Tag;
    private bool _isImporting;

    public TopBarViewModel(
        GalleryViewModel gallery,
        Func<Task> importAsync,
        NotificationService notifications,
        IAiService ai,
        AiAnalysisService aiAnalysis)
    {
        _gallery = gallery;
        _ai = ai;
        _aiAnalysis = aiAnalysis;
        Notifications = new NotificationListViewModel(notifications);
        ImportCommand = new AsyncRelayCommand(importAsync, () => !_isImporting);
        NotificationsCommand = new RelayCommand(() => Notifications.TogglePopup());
        // W3 settings window; App creates the dialog on this event.
        SettingsCommand = new RelayCommand(
            () => SettingsRequested?.Invoke());
        // AI batch analysis — enabled only when the backend is available.
        AiCommand = new AsyncRelayCommand(RunAiAnalysisAsync, () => _ai.IsAvailable && !_aiAnalysis.IsRunning);

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SEARCH_DEBOUNCE_MS) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await CommitSearchAsync();
        };
    }

    public AsyncRelayCommand ImportCommand { get; }
    public NotificationListViewModel Notifications { get; }
    public RelayCommand NotificationsCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public AsyncRelayCommand AiCommand { get; }

    public bool IsImporting
    {
        get => _isImporting;
        set
        {
            if (SetProperty(ref _isImporting, value))
                ImportCommand.RaiseCanExecuteChanged();
        }
    }

    public string AiDisabledReason
    {
        get
        {
            if (!_ai.IsAvailable)
                return "AI 未配置：请在设置中选择云端或本地模式并完成配置。";
            return string.Empty;
        }
    }

    private async Task RunAiAnalysisAsync()
    {
        if (!_ai.IsAvailable || _aiAnalysis.IsRunning) return;
        AiCommand.RaiseCanExecuteChanged();
        await _aiAnalysis.RunBatchAsync();
        AiCommand.RaiseCanExecuteChanged();
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    }

    public IReadOnlyList<SearchModeOption> SearchModes { get; } =
    [
        new(SearchMode.Tag, "标签"),
        new(SearchMode.FileNameNotes, "文件名·备注"),
    ];

    public SearchMode SelectedSearchMode
    {
        get => _searchMode;
        set
        {
            if (SetProperty(ref _searchMode, value))
                _ = CommitSearchAsync();
        }
    }

    public bool IsGridView
    {
        get => _gallery.IsGridView;
        set => _gallery.IsGridView = value;
    }

    /// <summary>Ctrl+F: the view subscribes and moves focus to the search box.</summary>
    public event Action? FocusSearchRequested;

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke();

    /// <summary>Shell wires this to open W3.</summary>
    public event Action? SettingsRequested;

    public void ClearSearchText()
    {
        _searchTimer.Stop();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
    }

    private async Task CommitSearchAsync()
    {
        _gallery.Filter.Text = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText;
        _gallery.Filter.SearchMode = _searchMode;
        await _gallery.RefreshAsync();
    }
}

public sealed record SearchModeOption(SearchMode Mode, string Display);
