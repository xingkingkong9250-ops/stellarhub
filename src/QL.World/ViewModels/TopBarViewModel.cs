using System.Windows.Threading;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Colors;

namespace QL.World.ViewModels;

/// <summary>A1 top bar (import / AI / view mode / search / notifications / settings).</summary>
public sealed class TopBarViewModel : ObservableObject
{
    private const int SEARCH_DEBOUNCE_MS = 300;

    private readonly GalleryViewModel _gallery;
    private readonly NotificationService _notifications;
    private readonly IAiService _ai;
    private readonly AiAnalysisService _aiAnalysis;
    private readonly ColorAnalysisService _colors;
    private readonly SemanticSearchService _semanticSearch;
    private readonly DispatcherTimer _searchTimer;
    private string _searchText = string.Empty;
    private SearchMode _searchMode = SearchMode.Tag;
    private bool _isImporting;
    private bool _isColorRunning;
    private IReadOnlyList<SearchModeOption> _searchModes = DefaultSearchModes();

    public TopBarViewModel(
        GalleryViewModel gallery,
        Func<Task> importAsync,
        NotificationService notifications,
        IAiService ai,
        AiAnalysisService aiAnalysis,
        ColorAnalysisService colors,
        SemanticSearchService semanticSearch)
    {
        _gallery = gallery;
        _notifications = notifications;
        _ai = ai;
        _aiAnalysis = aiAnalysis;
        _colors = colors;
        _semanticSearch = semanticSearch;
        Notifications = new NotificationListViewModel(notifications);
        ImportCommand = new AsyncRelayCommand(importAsync, () => !_isImporting);
        NotificationsCommand = new RelayCommand(() => Notifications.TogglePopup());
        // W3 settings window; App creates the dialog on this event.
        SettingsCommand = new RelayCommand(
            () => SettingsRequested?.Invoke());
        // AI batch analysis — enabled only when the backend is available.
        AiCommand = new AsyncRelayCommand(RunAiAnalysisAsync, () => _ai.IsAvailable && !_aiAnalysis.IsRunning);
        // Dominant-color backfill — independent of the AI mode (PRD).
        ColorCommand = new AsyncRelayCommand(RunColorAnalysisAsync, () => !_isColorRunning);

        // 语义档仅在「AI 档支持文本 embedding」或「库中已有 image embedding」时才显示。
        // 前者覆盖 Cloud 配置好的场景；后者覆盖 Disabled/Local 但库里有历史 embedding 的场景。
        _searchModes = BuildSearchModes();

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
    public AsyncRelayCommand ColorCommand { get; }

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

    private async Task RunColorAnalysisAsync()
    {
        if (_isColorRunning) return;
        _isColorRunning = true;
        ColorCommand.RaiseCanExecuteChanged();
        try
        {
            int written = await _colors.RunPendingAsync();
            _notifications.Publish(NotificationKind.ColorAnalysisCompleted, "颜色分析完成",
                $"共回填 {written} 张图片的主色调。");
        }
        finally
        {
            _isColorRunning = false;
            ColorCommand.RaiseCanExecuteChanged();
        }
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

    public IReadOnlyList<SearchModeOption> SearchModes
    {
        get => _searchModes;
        private set
        {
            if (SetProperty(ref _searchModes, value))
            {
                // 当前选中的档被剔除时回退到 Tag，避免 ComboBox 找不到 SelectedItem。
                if (!_searchModes.Any(o => o.Mode == _searchMode))
                {
                    _searchMode = SearchMode.Tag;
                    OnPropertyChanged(nameof(SelectedSearchMode));
                }
            }
        }
    }

    public SearchMode SelectedSearchMode
    {
        get => _searchMode;
        set
        {
            if (SetProperty(ref _searchMode, value))
                _ = CommitSearchAsync();
        }
    }

    /// <summary>Refresh the SearchModes list (called after AI config changes or embeddings land).</summary>
    public void RefreshSearchModes()
    {
        SearchModes = BuildSearchModes();
    }

    private static IReadOnlyList<SearchModeOption> DefaultSearchModes() =>
    [
        new(SearchMode.Tag, "标签"),
        new(SearchMode.FileNameNotes, "文件名·备注"),
    ];

    private IReadOnlyList<SearchModeOption> BuildSearchModes()
    {
        var list = new List<SearchModeOption>
        {
            new(SearchMode.Tag, "标签"),
            new(SearchMode.FileNameNotes, "文件名·备注"),
        };
        // 语义档：AI 档支持文本 embedding（Cloud 配置好），或库中已有 image embedding。
        // 前者覆盖文→图搜索；后者覆盖图→图相似搜索（任意档可用，依赖已存向量）。
        if (_ai.SupportsEmbeddings || _semanticSearch.HasAnyEmbeddings())
            list.Add(new SearchModeOption(SearchMode.Semantic, "语义"));
        return list;
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

    /// <summary>Called by ShellViewModel.FindSimilarAsync to reflect the new semantic filter state in the search box.</summary>
    public void SwitchToSemanticMode()
    {
        _searchTimer.Stop();
        _searchText = string.Empty;
        _searchMode = SearchMode.Semantic;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedSearchMode));
    }

    private async Task CommitSearchAsync()
    {
        // Semantic mode: clear text/tag filters; SemanticSearchService resolves the id list.
        if (_searchMode == SearchMode.Semantic)
        {
            _gallery.Filter.Text = null;
            _gallery.Filter.Tag = null;
            _gallery.Filter.SearchMode = SearchMode.Semantic;
            _gallery.Filter.SimilarToImageId = null;

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                // 空查询：清空语义结果（让 ImageQueryService 走 1=0 分支显示空）。
                _gallery.Filter.SemanticResultIds = null;
            }
            else
            {
                IReadOnlyList<long>? ids = await _semanticSearch.SearchByTextAsync(_searchText.Trim());
                if (ids is null)
                {
                    _notifications.Publish(NotificationKind.SemanticSearchUnavailable,
                        "语义搜索不可用", "AI 档不支持文本 embedding，或本地未实现（M3-E2）。");
                    // 回退到 Tag 模式让用户至少能用搜索。
                    _searchMode = SearchMode.Tag;
                    _gallery.Filter.SearchMode = SearchMode.Tag;
                    _gallery.Filter.Text = _searchText;
                    OnPropertyChanged(nameof(SelectedSearchMode));
                }
                else
                {
                    _gallery.Filter.SemanticResultIds = ids;
                }
            }

            await _gallery.RefreshAsync();
            return;
        }

        _gallery.Filter.Text = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText;
        _gallery.Filter.SearchMode = _searchMode;
        _gallery.Filter.SemanticResultIds = null;
        _gallery.Filter.SimilarToImageId = null;
        await _gallery.RefreshAsync();
    }
}

public sealed record SearchModeOption(SearchMode Mode, string Display);
