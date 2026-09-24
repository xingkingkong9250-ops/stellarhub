using System.IO;
using System.Windows;
using System.Windows.Threading;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.ViewModels.Support;

namespace QL.World.ViewModels;

/// <summary>
/// W1 shell: owns all sub-VMs, coordinates filter/refresh flows, context
/// actions, the import dialog lifecycle and session-scoped undo of removals.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private const int UNDO_WINDOW_SECONDS = 8;

    private readonly ServiceRegistry _services;
    private readonly DispatcherTimer _undoTimer;
    private RemovedEntryBundle? _lastRemoved;
    private bool _undoVisible;
    private string _undoText = string.Empty;
    private bool _disposed;

    public ShellViewModel(ServiceRegistry services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;

        ThumbnailProvider = new GalleryThumbnailProvider(services.Thumbnails);
        Gallery = new GalleryViewModel(services.ImageQuery, services.Settings, ThumbnailProvider);
        TopBar = new TopBarViewModel(Gallery, ImportAsync, services.Notifications, services.Ai,
            services.AiAnalysis, services.Colors, services.SemanticSearch);
        TagTree = new TagTreeViewModel(Gallery, services.ImageQuery, services.Tags);
        CollectionTree = new CollectionTreeViewModel(Gallery, services.Collections);
        PbrGroupTree = new PbrGroupTreeViewModel(Gallery, services.PbrGrouping);
        Detail = new DetailViewModel(services.ImageQuery, services.Tags, Gallery);
        StatusBar = new StatusBarViewModel(services.Thumbnails, services.Settings);

        Gallery.Refreshed += OnGalleryRefreshed;
        Gallery.SelectionChanged += OnSelectionChanged;
        TagTree.ClearFiltersRequested += ClearFilters;
        TopBar.SettingsRequested += () => ShowSettingsDialog?.Invoke();

        _undoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(UNDO_WINDOW_SECONDS),
        };
        _undoTimer.Tick += (_, _) => HideUndoBar();

        RefreshCommand = new AsyncRelayCommand(() => Gallery.RefreshAsync());
        FocusSearchCommand = new RelayCommand(() => TopBar.RequestFocusSearch());
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        OpenCommand = new RelayCommand(_ => OpenSelected(), _ => HasSelection());
        RevealCommand = new RelayCommand(_ => RevealSelected(), _ => HasSelection());
        CopyPathCommand = new RelayCommand(_ => CopySelectedPath(), _ => HasSelection());
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => HasSelection());
        RegenerateThumbnailsCommand = new RelayCommand(_ => RegenerateThumbnails(), _ => HasSelection());
        FindSimilarCommand = new AsyncRelayCommand(
            FindSimilarAsync,
            () => HasSelection() && _services.SemanticSearch.HasAnyEmbeddings());
        SetRatingCommand = new RelayCommand(SetRatings, _ => HasSelection());
        SetCategoryCommand = new RelayCommand(SetCategories, _ => HasSelection());
        UndoCommand = new RelayCommand(UndoRemove, () => _undoVisible);

        CreateCollectionCommand = new RelayCommand(CreateCollection);
        RenameCollectionCommand = new RelayCommand(RenameCollection);
        DeleteCollectionCommand = new RelayCommand(DeleteCollection);
        SetCollectionCoverCommand = new RelayCommand(SetCollectionCover);
        AddSelectedToCollectionCommand = new RelayCommand(AddSelectedToCollection);
    }

    public AppSettingsService Settings => _services.Settings;

    /// <summary>B2 context menu category submenu.</summary>
    public IReadOnlyList<CategoryMenuItemOption> CategoryMenuOptions { get; } =
        [new("未分类", null), .. Categories.All.Select(c => new CategoryMenuItemOption(c, c))];

    /// <summary>B2 context menu rating submenu.</summary>
    public IReadOnlyList<RatingMenuItemOption> RatingMenuOptions { get; } =
    [
        new("清除评分", 0),
        new("1 ★", 1),
        new("2 ★", 2),
        new("3 ★", 3),
        new("4 ★", 4),
        new("5 ★", 5),
    ];

    public GalleryThumbnailProvider ThumbnailProvider { get; }
    public GalleryViewModel Gallery { get; }
    public TopBarViewModel TopBar { get; }
    public TagTreeViewModel TagTree { get; }
    public CollectionTreeViewModel CollectionTree { get; }
    public PbrGroupTreeViewModel PbrGroupTree { get; }
    public DetailViewModel Detail { get; }
    public StatusBarViewModel StatusBar { get; }

    public RelayCommand FocusSearchCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RegenerateThumbnailsCommand { get; }
    public AsyncRelayCommand FindSimilarCommand { get; }
    public RelayCommand SetRatingCommand { get; }
    public RelayCommand SetCategoryCommand { get; }
    public RelayCommand UndoCommand { get; }

    public RelayCommand CreateCollectionCommand { get; }
    public RelayCommand RenameCollectionCommand { get; }
    public RelayCommand DeleteCollectionCommand { get; }
    public RelayCommand SetCollectionCoverCommand { get; }
    public RelayCommand AddSelectedToCollectionCommand { get; }

    /// <summary>App wires this to show W2; returns count of added images.</summary>
    public event Action<ImportViewModel>? ShowImportDialog;

    /// <summary>App wires this to show InputDialog. Returns null on cancel.</summary>
    public event Func<string, string, string, string?>? RequestInputDialog;

    /// <summary>App wires this to show the W3 settings window.</summary>
    public event Action? ShowSettingsDialog;

    public string Title => $"{AppInfo.ProductName} — {AppInfo.ProductSubtitle}";
    public string VersionText => $"v{AppInfo.Version}";
    public string AiStatusText => "AI：关闭";

    public bool UndoVisible
    {
        get => _undoVisible;
        private set => SetProperty(ref _undoVisible, value);
    }

    public string UndoText
    {
        get => _undoText;
        private set => SetProperty(ref _undoText, value);
    }

    public async Task InitializeAsync()
    {
        await Gallery.RefreshAsync();
        StatusBar.PublishMessage("就绪");
    }

    // ─── Import ──────────────────────────────────────────────────────────────

    private async Task ImportAsync()
    {
        if (TopBar.IsImporting) return;
        TopBar.IsImporting = true;
        try
        {
            var vm = new ImportViewModel(_services.Import, GetSuggestedImportFolder);
            ShowImportDialog?.Invoke(vm);

            // The dialog drives completion; reopen guards are handled there.
            await Task.CompletedTask;
            if (vm.AddedOnClose > 0)
            {
                await Gallery.RefreshAsync();
                StatusBar.PublishMessage($"已导入 {vm.AddedOnClose} 张图片");
                RunColorBackfill();
            }
        }
        finally
        {
            TopBar.IsImporting = false;
        }
    }

    /// <summary>Fire-and-forget dominant-color backfill after new imports.</summary>
    private void RunColorBackfill()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _services.Colors.RunPendingAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[color] backfill failed: {ex.Message}");
            }
        });
    }

    private string GetSuggestedImportFolder()
    {
        GalleryItemViewModel? first = Gallery.PrimarySelected;
        if (first is not null)
        {
            string? dir = Path.GetDirectoryName(first.FilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                return dir;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    // ─── Context actions ───────────────────────────────────────────────────

    private bool HasSelection() => Gallery.SelectedItems.Count > 0;

    private void OpenSelected()
    {
        foreach (GalleryItemViewModel item in Gallery.SelectedItems)
            SystemNavigator.OpenFile(item.FilePath);
    }

    private void RevealSelected()
    {
        if (Gallery.PrimarySelected is { } primary)
            SystemNavigator.RevealFile(primary.FilePath);
    }

    private void CopySelectedPath()
    {
        if (Gallery.PrimarySelected is { } primary)
            Clipboard.SetText(primary.FilePath);
    }

    private void RemoveSelected()
    {
        List<GalleryItemViewModel> selection = Gallery.SelectedItems.ToList();
        if (selection.Count == 0) return;

        var result = MessageBox.Show(
            $"确定从库中移除选中的 {selection.Count} 张图片吗？\n（仅移除记录与缩略图，不删除磁盘文件，{UNDO_WINDOW_SECONDS} 秒内可撤销）",
            "移除图片",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;

        List<long> ids = selection.Select(i => i.Id).ToList();
        _lastRemoved = _services.ImageQuery.Remove(ids);
        _ = Gallery.RefreshAsync();

        ShowUndoBar(ids.Count);
        StatusBar.PublishMessage($"已移除 {ids.Count} 张");
    }

    private void RegenerateThumbnails()
    {
        foreach (GalleryItemViewModel item in Gallery.SelectedItems)
            _services.Thumbnails.Regenerate(item.FileHash, item.FilePath);
        StatusBar.PublishMessage("已重新排队生成缩略图");
    }

    private void SetRatings(object? parameter)
    {
        if (parameter is null) return;
        int rating = Convert.ToInt32(parameter);
        List<long> ids = Gallery.SelectedItems.Select(i => i.Id).ToList();
        _services.ImageQuery.SetRatingBatch(ids, rating);
        _ = Gallery.RefreshCurrentAsync();
    }

    private void SetCategories(object? parameter)
    {
        string? category = parameter as string;
        if (category is not null && !Categories.IsKnown(category)) return;
        List<long> ids = Gallery.SelectedItems.Select(i => i.Id).ToList();
        _services.ImageQuery.SetCategoryBatch(ids, category);
        _ = Gallery.RefreshCurrentAsync();
    }

    private void UndoRemove()
    {
        if (_lastRemoved is null) return;
        int restored = _services.ImageQuery.Restore(_lastRemoved);
        _lastRemoved = null;
        HideUndoBar();
        _ = Gallery.RefreshAsync();
        StatusBar.PublishMessage(
            restored > 0 ? $"已恢复 {restored} 张图片" : "没有可恢复的图片（路径可能已冲突）");
    }

    // ─── Collection actions ─────────────────────────────────────────────────

    private void CreateCollection()
    {
        string? name = RequestInputDialog?.Invoke("新建收藏夹", "请输入收藏夹名称：", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        _services.Collections.Create(name);
        _ = CollectionTree.RefreshAsync();
        StatusBar.PublishMessage("已创建收藏夹");
    }

    private void RenameCollection(object? parameter)
    {
        if (parameter is not CollectionNodeViewModel node) return;
        string? newName = RequestInputDialog?.Invoke("重命名收藏夹", "请输入新名称：", node.DisplayName);
        if (string.IsNullOrWhiteSpace(newName)) return;

        _services.Collections.Rename(node.Id, newName);
        _ = CollectionTree.RefreshAsync();
        StatusBar.PublishMessage("已重命名收藏夹");
    }

    private void DeleteCollection(object? parameter)
    {
        if (parameter is not CollectionNodeViewModel node) return;
        var result = MessageBox.Show(
            $"确定删除收藏夹「{node.DisplayName}」吗？\n（库中图片不会被删除，仅移除收藏夹分组）",
            "删除收藏夹",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;

        _services.Collections.Delete(node.Id);
        if (Gallery.Filter.CollectionId == node.Id)
            Gallery.Filter.CollectionId = null;
        _ = CollectionTree.RefreshAsync();
        StatusBar.PublishMessage("已删除收藏夹");
    }

    private void SetCollectionCover(object? parameter)
    {
        if (parameter is not CollectionNodeViewModel node) return;
        if (Gallery.PrimarySelected is not { } primary) return;

        _services.Collections.SetCover(node.Id, primary.Id);
        _ = CollectionTree.RefreshAsync();
        StatusBar.PublishMessage("已更新封面");
    }

    private void AddSelectedToCollection(object? parameter)
    {
        if (parameter is not CollectionNodeViewModel node) return;
        List<long> ids = Gallery.SelectedItems.Select(i => i.Id).ToList();
        if (ids.Count == 0) return;

        _services.Collections.AddImages(node.Id, ids);
        _ = CollectionTree.RefreshAsync();
        StatusBar.PublishMessage($"已加入收藏夹「{node.DisplayName}」（{ids.Count} 张）");
    }

    // ─── Wiring ──────────────────────────────────────────────────────────────

    private void OnGalleryRefreshed()
    {
        StatusBar.PublishCounts(Gallery.TotalCount, Gallery.FilteredCount);
        StatusBar.UpdateShortcutHint(Gallery.SelectedItems.Count, Gallery.TotalCount);
        _ = TagTree.RefreshCountsAsync();
        _ = CollectionTree.RefreshAsync();
        _ = PbrGroupTree.RefreshAsync();
    }

    private void OnSelectionChanged()
        => StatusBar.UpdateShortcutHint(Gallery.SelectedItems.Count, Gallery.TotalCount);

    private void ClearFilters()
    {
        Gallery.Filter.Category = null;
        Gallery.Filter.Uncategorized = false;
        Gallery.Filter.Tag = null;
        Gallery.Filter.CollectionId = null;
        Gallery.Filter.PbrGroupId = null;
        Gallery.Filter.Text = null;
        Gallery.Filter.SemanticResultIds = null;
        Gallery.Filter.SimilarToImageId = null;
        TopBar.ClearSearchText();
        TagTree.ResetSelection();
        CollectionTree.ResetSelection();
        PbrGroupTree.ResetSelection();
        _ = Gallery.RefreshAsync();
    }

    /// <summary>
    /// W1/B2-3-CM1 图卡右键「找相似图」/ W1/B3-8 详情栏「找相似」按钮。
    /// 调 SemanticSearchService.SearchByImageId 获取 top-50 相似图片 id，
    /// 设置 Gallery.Filter 进入语义模式并刷新画廊。
    /// </summary>
    private async Task FindSimilarAsync()
    {
        GalleryItemViewModel? primary = Gallery.PrimarySelected;
        if (primary is null) return;

        IReadOnlyList<long>? ids = _services.SemanticSearch.SearchByImageId(primary.Id);
        if (ids is null || ids.Count == 0)
        {
            _services.Notifications.Publish(NotificationKind.SemanticSearchUnavailable,
                "找相似图不可用", "暂无已分析图片。请先运行 AI 分析后再试。");
            return;
        }

        // 清除其它筛选条件，进入语义模式。
        Gallery.Filter.Category = null;
        Gallery.Filter.Uncategorized = false;
        Gallery.Filter.Tag = null;
        Gallery.Filter.CollectionId = null;
        Gallery.Filter.PbrGroupId = null;
        Gallery.Filter.Text = null;
        Gallery.Filter.SemanticResultIds = ids;
        Gallery.Filter.SimilarToImageId = primary.Id;
        Gallery.Filter.SearchMode = SearchMode.Semantic;
        TopBar.SwitchToSemanticMode();
        await Gallery.RefreshAsync();
    }

    private void ShowUndoBar(int count)
    {
        UndoText = $"已移除 {count} 张图片";
        UndoVisible = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void HideUndoBar()
    {
        _undoTimer.Stop();
        UndoVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _undoTimer.Stop();
        Gallery.Refreshed -= OnGalleryRefreshed;
        Gallery.SelectionChanged -= OnSelectionChanged;
        TagTree.ClearFiltersRequested -= ClearFilters;
        ThumbnailProvider.Dispose();
    }
}

public sealed record CategoryMenuItemOption(string Display, string? Value);

public sealed record RatingMenuItemOption(string Display, int Value);
