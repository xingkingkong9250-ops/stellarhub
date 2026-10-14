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
    private readonly AppSettingsService _settings;
    private readonly DispatcherTimer _searchTimer;
    private string _searchText = string.Empty;
    private bool _isImporting;
    private bool _isColorRunning;
    private int? _selectedRecentImport;

    public TopBarViewModel(
        GalleryViewModel gallery,
        Func<Task> importAsync,
        NotificationService notifications,
        IAiService ai,
        AiAnalysisService aiAnalysis,
        ColorAnalysisService colors,
        SemanticSearchService semanticSearch,
        AppSettingsService settings)
    {
        _gallery = gallery;
        _notifications = notifications;
        _ai = ai;
        _aiAnalysis = aiAnalysis;
        _colors = colors;
        _semanticSearch = semanticSearch;
        _settings = settings;
        // U2-B: restore 最近入库 dropdown from persisted settings (null = 全部).
        _selectedRecentImport = settings.GalleryRecentImportDays;
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

        // F-14: IsGridView is a pass-through to Gallery; without forwarding,
        // the B1 view-toggle bound to TopBar.IsGridView never re-pulls when the
        // mode changes via the Gallery.IsListView-bound counterpart, so both
        // buttons could render checked at once. Forward the notification.
        // B2-3-BRW: same forwarding for the new Browse toggle (List toggle binds
        // Gallery.IsListView directly, so we don't mirror IsListView on TopBar).
        _gallery.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GalleryViewModel.IsGridView)
                or nameof(GalleryViewModel.IsListView)
                or nameof(GalleryViewModel.IsBrowseView))
            {
                OnPropertyChanged(nameof(IsGridView));
                OnPropertyChanged(nameof(IsBrowseView));
            }
        };

        // v0.14.6: 不再手动选择搜索模式，由 SearchQueryParser 自动识别。

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

    /// <summary>v0.14.6: 立即提交搜索（Enter 键触发，跳过防抖定时器）。</summary>
    public void CommitSearchNow()
    {
        _searchTimer.Stop();
        _ = CommitSearchAsync();
    }

    public bool IsGridView
    {
        get => _gallery.IsGridView;
        set => _gallery.IsGridView = value;
    }

    /// <summary>B2-3-BRW: pass-through to Gallery; bound by A1-3 「浏览」 toggle.</summary>
    public bool IsBrowseView
    {
        get => _gallery.IsBrowseView;
        set => _gallery.IsBrowseView = value;
    }

    // ─── U2-B (§4.B.1): 最近入库 dropdown ───────────────────────────────────

    public IReadOnlyList<RecentImportOption> RecentImportOptions { get; } =
    [
        new(null, "全部"),
        new(1, "今天"),
        new(7, "最近 7 天"),
        new(30, "最近 30 天"),
    ];

    public int? SelectedRecentImport
    {
        get => _selectedRecentImport;
        set
        {
            if (SetProperty(ref _selectedRecentImport, value))
            {
                _gallery.Filter.ImportedWithinDays = value;
                _settings.GalleryRecentImportDays = value;
                _ = _gallery.RefreshAsync();
            }
        }
    }

    /// <summary>Ctrl+F: the view subscribes and moves focus to the search box.</summary>
    public event Action? FocusSearchRequested;

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke();

    /// <summary>Shell wires this to open W3.</summary>
    public event Action? SettingsRequested;

    /// <summary>U6-D: Shell supplies the current scope; when false searches stay out of the local DB.</summary>
    public Func<bool>? LocalScopeProvider;

    /// <summary>U6-D: fired with the box text while the shared scope is active.</summary>
    public event Action<string>? SharedSearchCommitted;

    public void ClearSearchText()
    {
        _searchTimer.Stop();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
    }

    /// <summary>U2-B: reset 最近入库 dropdown to 全部 (called by 清除全部筛选).</summary>
    public void ClearRecentImport()
    {
        if (_selectedRecentImport is not null)
        {
            _selectedRecentImport = null;
            OnPropertyChanged(nameof(SelectedRecentImport));
        }
    }

    /// <summary>Called by ShellViewModel.FindSimilarAsync to reflect the new semantic filter state in the search box.</summary>
    public void SwitchToSemanticMode()
    {
        _searchTimer.Stop();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
    }

    private async Task CommitSearchAsync()
    {
        // U6-D: in shared-library scope the box is an in-memory name/tag
        // filter over the loaded shared items, never a local-DB query.
        if (LocalScopeProvider is not null && !LocalScopeProvider())
        {
            SharedSearchCommitted?.Invoke(_searchText);
            return;
        }

        // v0.14.6: SearchQueryParser 自动识别意图，不再手动选择模式。
        SearchQuery query = SearchQueryParser.Parse(_searchText);

        // 空查询：清空所有搜索过滤
        if (string.IsNullOrEmpty(query.Text))
        {
            _gallery.Filter.Text = null;
            _gallery.Filter.SearchMode = SearchMode.Tag;
            _gallery.Filter.SearchPinyin = null;
            _gallery.Filter.SemanticResultIds = null;
            _gallery.Filter.SimilarToImageId = null;
            await _gallery.RefreshAsync();
            return;
        }

        // 默认走多字段 OR 搜索（标签+文件名+备注+分类+拼音）
        _gallery.Filter.Text = query.Text;
        _gallery.Filter.SearchMode = query.Mode;
        _gallery.Filter.SearchPinyin = query.Pinyin;
        _gallery.Filter.SemanticResultIds = null;
        _gallery.Filter.SimilarToImageId = null;

        await _gallery.RefreshAsync();

        // v0.14.6 Phase 5: 精确匹配无结果且 AI 可用时，自动降级语义搜索
        // （仅当用户输入较长查询时才触发，避免短查询频繁降级）
        if (query.Text.Length >= 4 && _gallery.TotalCount == 0
            && (_ai.SupportsEmbeddings || _semanticSearch.HasAnyEmbeddings()))
        {
            IReadOnlyList<long>? ids = await _semanticSearch.SearchByTextAsync(query.Text);
            if (ids is { Count: > 0 })
            {
                _gallery.Filter.Text = null;
                _gallery.Filter.SearchMode = SearchMode.Semantic;
                _gallery.Filter.SearchPinyin = null;
                _gallery.Filter.SemanticResultIds = ids;
                await _gallery.RefreshAsync();
            }
        }
    }
}

public sealed record SearchModeOption(SearchMode Mode, string Display);

public sealed record RecentImportOption(int? Days, string Display);
