using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
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

        ThumbnailProvider = new GalleryThumbnailProvider(services.Thumbnails, services.NasFactory, services.Settings, services.NetworkMonitor);
        Gallery = new GalleryViewModel(services.ImageQuery, services.Settings, ThumbnailProvider);
        TopBar = new TopBarViewModel(Gallery, ImportAsync, services.Notifications, services.Ai,
            services.AiAnalysis, services.Colors, services.SemanticSearch, services.Settings);
        TagTree = new TagTreeViewModel(Gallery, services.ImageQuery, services.Tags);
        CollectionTree = new CollectionTreeViewModel(Gallery, services.Collections);
        PbrGroupTree = new PbrGroupTreeViewModel(Gallery, services.PbrGrouping);
        FilterGroups = new FilterGroupsPanelViewModel(Gallery, services.Settings);
        Detail = new DetailViewModel(services.ImageQuery, services.Tags, Gallery, services.SemanticSearch);
        StatusBar = new StatusBarViewModel(services.Thumbnails, services.Settings);
        // U2-A: surface AI/import running state on C1 progress bar without coupling StatusBar to those services.
        StatusBar.IsBusyPredicate = () => TopBar.IsImporting || services.AiAnalysis.IsRunning;
        // W2/AI (v0.9.0): AI 批量分析进度 → 底部信息提示栏（INPC 属性更新
        // 由 WPF 自动跨线程封送，服务端在后台线程触发安全）。
        services.AiAnalysis.BatchProgress += (processed, total) =>
            StatusBar.PublishMessage($"AI 分析：{processed}/{total} 张");

        Gallery.Refreshed += OnGalleryRefreshed;
        Gallery.SelectionChanged += OnSelectionChanged;
        TagTree.ClearFiltersRequested += ClearFilters;
        TopBar.SettingsRequested += () => ShowSettingsDialog?.Invoke();
        // U6-D: search box in shared scope filters shared items in memory.
        TopBar.LocalScopeProvider = () => IsLocalScope;
        TopBar.SharedSearchCommitted += text => SharedGallery?.ApplyFilter(text);
        // U2-C: clicking a related-asset thumbnail in B3-9 dives into its similarity neighborhood.
        Detail.FindSimilarRequested += relatedId => _ = FindSimilarAsync(relatedId);

        // U3-C (§4.E.3): collapsed sidebar headers show child counts — refresh
        // the (N) suffix whenever a section's items change.
        TagTree.CategoryNodes.CollectionChanged +=
            (_, _) => OnPropertyChanged(nameof(LibraryHeaderText));
        CollectionTree.CollectionNodes.CollectionChanged +=
            (_, _) => OnPropertyChanged(nameof(CollectionsHeaderText));
        TagTree.TopTagNodes.CollectionChanged +=
            (_, _) => OnPropertyChanged(nameof(TopTagsHeaderText));
        PbrGroupTree.GroupNodes.CollectionChanged +=
            (_, _) => OnPropertyChanged(nameof(PbrGroupsHeaderText));

        _undoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(UNDO_WINDOW_SECONDS),
        };
        _undoTimer.Tick += (_, _) => HideUndoBar();

        // A1-10 (v0.8.0): 主动刷新——重扫监控目录（幂等）+ 重载画廊；F5 同命令。
        RefreshCommand = new AsyncRelayCommand(RefreshLibraryAsync, () => !TopBar.IsImporting);
        ScanNasLibraryCommand = new AsyncRelayCommand(ScanNasLibraryAsync, () => !TopBar.IsImporting);
        FocusSearchCommand = new RelayCommand(() => TopBar.RequestFocusSearch());
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        // M5-G: opens the persistent download queue window.
        ShowDownloadQueueCommand = new RelayCommand(() => ShowDownloadQueueDialog?.Invoke());

        OpenCommand = new RelayCommand(_ => OpenSelected(), _ => HasSelection());
        RevealCommand = new RelayCommand(_ => RevealSelected(), _ => HasSelection());
        CopyPathCommand = new RelayCommand(_ => CopySelectedPath(), _ => HasSelection());
        // U3-B (§4.C.1): hover action bar commands act on the hovered card
        // (CommandParameter), independent of the current selection.
        OpenItemCommand = new RelayCommand(OpenItem, CanActOnItem);
        RevealItemCommand = new RelayCommand(RevealItem, CanActOnItem);
        CopyItemPathCommand = new RelayCommand(CopyItemPath, CanActOnItem);
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => HasSelection());
        RegenerateThumbnailsCommand = new RelayCommand(_ => RegenerateThumbnails(), _ => HasSelection());
        FindSimilarCommand = new AsyncRelayCommand(
            () => FindSimilarAsync(),
            () => HasSelection() && _services.SemanticSearch.HasAnyEmbeddings());
        SetRatingCommand = new RelayCommand(SetRatings, _ => HasSelection());
        SetCategoryCommand = new RelayCommand(SetCategories, _ => HasSelection());
        UndoCommand = new RelayCommand(UndoRemove, () => _undoVisible);

        CreateCollectionCommand = new RelayCommand(CreateCollection);
        RenameCollectionCommand = new RelayCommand(RenameCollection);
        DeleteCollectionCommand = new RelayCommand(DeleteCollection);
        SetCollectionCoverCommand = new RelayCommand(SetCollectionCover);
        AddSelectedToCollectionCommand = new RelayCommand(AddSelectedToCollection);

        // PEND-004 (0.18.0): shared collections on the NAS (collections.json).
        CreateSharedCollectionCommand = new AsyncRelayCommand(CreateSharedCollectionAsync);
        DeleteSharedCollectionCommand = new AsyncRelayCommand(DeleteSharedCollectionAsync);
        AddSelectedToSharedCollectionCommand = new AsyncRelayCommand(AddSelectedToSharedCollectionAsync);
        RemoveSelectedFromSharedCollectionCommand = new AsyncRelayCommand(RemoveSelectedFromSharedCollectionAsync);

        // M6-D / U6-A: shared library (NAS). Available only when logged in with a NAS account.
        // M8-C: SharedDetailViewModel 改用 NasConnectionRegistry（按 SourceNasId 路由）。
        IsSharedLibraryAvailable = _services.SharedLibrary is not null;
        if (IsSharedLibraryAvailable && _services.SharedStorage is { } sharedStorage && _services.NasRegistry is { } nasRegistry)
        {
            SharedThumbnails = new SharedThumbnailCache(
                sharedStorage.Connector,
                Path.Combine(_services.Settings.DataRoot, "shared-thumbs"),
                _services.NetworkMonitor);
            SharedGallery = new SharedGalleryViewModel(_services.Settings, SharedThumbnails);
            SharedDetail = new SharedDetailViewModel(SharedGallery, nasRegistry);
            SharedDetail.RequestSaveFile += defaultName =>
                RequestSaveCopyDialog?.Invoke(defaultName);
            SharedDetail.SaveCompleted += (kind, title, message) =>
                _services.Notifications.Publish(kind, title, message);
        }
        SwitchToLocalCommand = new RelayCommand(() => IsLocalScope = true);
        SwitchToSharedCommand = new RelayCommand(() => IsLocalScope = false,
            () => IsSharedLibraryAvailable);
        // U6-B/F-11: menu opens with ContextMenu.DataContext = ShellViewModel; the clicked item
        // is taken from the current selection instead of a CommandParameter.
        SubmitToSharedCommand = new AsyncRelayCommand(
            SubmitToSharedAsync,
            () => CanSubmitToShared);
        SubmitToSecondaryNasCommand = new AsyncRelayCommand<NasConnectionSpec?>(
            SubmitToSecondaryNasAsync,
            spec => _services.Permissions.CanSubmitToSecondaryNas && spec is not null && Gallery.PrimarySelected is not null);
        if (IsSharedLibraryAvailable)
        {
            _ = LoadSharedGalleryAsync();
            _ = LoadSharedCollectionsAsync();
        }

        // U6-C: admin review queue entry + pending badge count.
        IsReviewAvailable = _services.Permissions.CanApproveSubmissions;
        ShowReviewQueueCommand = new RelayCommand(
            () => ShowReviewQueueDialog?.Invoke(),
            () => IsReviewAvailable);
        if (IsReviewAvailable)
            _ = RefreshPendingCountAsync();

        // 0.16.0 DUP-D: W8 重复审核入口。L1 感知哈希恒可用，AI 不可用也不置灰。
        ShowDuplicateReviewCommand = new RelayCommand(
            () => ShowDuplicateReviewDialog?.Invoke());

        // 0.17.0 PEND-002: W9 素材组织窗口（收藏夹/高频标签/贴图组）。
        ShowMaterialOrganizerCommand = new RelayCommand(
            () => ShowMaterialOrganizerDialog?.Invoke());
    }

    public AppSettingsService Settings => _services.Settings;

    /// <summary>B2 context menu category submenu. M12: 标签驱动 6 新分类。</summary>
    public IReadOnlyList<CategoryMenuItemOption> CategoryMenuOptions { get; } =
        [new("未分类", null), .. Models.Taxonomy.TagTaxonomy.Categories
            .Select(c => new CategoryMenuItemOption(c, c))];

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
    public FilterGroupsPanelViewModel FilterGroups { get; }
    public DetailViewModel Detail { get; }
    public StatusBarViewModel StatusBar { get; }

    // ─── M6-D / U6-A: shared library ─────────────────────────────────────

    /// <summary>True when the user logged in with a NAS account (shared library available).</summary>
    public bool IsSharedLibraryAvailable { get; }

    /// <summary>
    /// M8-D: 当前登录用户是否管理员。供 UI 绑定「提交到副库」菜单项的 Visibility
    /// （仅 admin 可见）。普通用户该菜单项折叠。
    /// </summary>
    public bool IsAdmin => _services.Permissions.IsAdmin;

    /// <summary>
    /// M8-D: 当前所有副库（Role==Secondary）连接配置，供「提交到副库」子菜单绑定。
    /// ShareLink 副库不含（无写权限）。每次访问现算以保持与配置同步。
    /// </summary>
    public IReadOnlyList<NasConnectionSpec> SecondaryNasConnections
        => _services.Settings.NasConnections
            .Where(c => c.Role == NasRole.Secondary)
            .ToList();

    /// <summary>Gallery VM for the NAS shared library; null in local-only mode.</summary>
    public SharedGalleryViewModel? SharedGallery { get; }

    /// <summary>U6-D: read-only shared detail pane; null in local-only mode.</summary>
    public SharedDetailViewModel? SharedDetail { get; }

    /// <summary>U6-C: NAS thumbnail cache (also feeds the review window).</summary>
    public SharedThumbnailCache? SharedThumbnails { get; }

    // ─── PEND-004 (0.18.0): shared collections ───────────────────────────

    /// <summary>Shared collection picker options (first entry = "all images").</summary>
    public ObservableCollection<SharedCollectionOption> SharedCollectionOptions { get; } = [];

    private SharedCollectionOption? _selectedSharedCollection;
    /// <summary>Currently selected shared collection; null/id-null means "all images".</summary>
    public SharedCollectionOption? SelectedSharedCollection
    {
        get => _selectedSharedCollection;
        set
        {
            if (SetProperty(ref _selectedSharedCollection, value))
            {
                OnPropertyChanged(nameof(IsSharedCollectionSelected));
                SharedGallery?.ApplyCollectionFilter(value?.Source?.ImageIds);
            }
        }
    }

    /// <summary>True when a real collection (not "all") is selected.</summary>
    public bool IsSharedCollectionSelected => _selectedSharedCollection?.Id is not null;

    public AsyncRelayCommand CreateSharedCollectionCommand { get; }
    public AsyncRelayCommand DeleteSharedCollectionCommand { get; }
    public AsyncRelayCommand AddSelectedToSharedCollectionCommand { get; }
    public AsyncRelayCommand RemoveSelectedFromSharedCollectionCommand { get; }

    /// <summary>U6-C: true for admins (review queue entry visible).</summary>
    public bool IsReviewAvailable { get; }

    /// <summary>U6-C: opens the W6 review queue window.</summary>
    public RelayCommand ShowReviewQueueCommand { get; }

    /// <summary>U6-C: bound by App to instantiate <see cref="Views.ReviewQueueWindow"/>.</summary>
    public event Action? ShowReviewQueueDialog;

    /// <summary>0.16.0 DUP-D: opens the W8 duplicate review window.</summary>
    public RelayCommand ShowDuplicateReviewCommand { get; }

    /// <summary>0.16.0 DUP-D: bound by App to instantiate <see cref="Views.DuplicateReviewWindow"/>.</summary>
    public event Action? ShowDuplicateReviewDialog;

    /// <summary>0.17.0 PEND-002: opens the W9 material organizer window.</summary>
    public RelayCommand ShowMaterialOrganizerCommand { get; }

    /// <summary>0.17.0 PEND-002: bound by App to instantiate <see cref="Views.MaterialOrganizerWindow"/>.</summary>
    public event Action? ShowMaterialOrganizerDialog;

    private int _pendingCount;
    /// <summary>U6-C: number of submissions awaiting review (toolbar badge).</summary>
    public int PendingCount
    {
        get => _pendingCount;
        private set => SetProperty(ref _pendingCount, value);
    }

    /// <summary>U6-C: reloads the pending badge count (also called when the review window closes).</summary>
    public async Task RefreshPendingCountAsync()
    {
        if (_services.Submissions is null) return;
        try
        {
            var pending = await _services.Submissions
                .ListPendingAsync(CancellationToken.None)
                .ConfigureAwait(true);
            PendingCount = pending.Count;
        }
        catch (NasConnectionException)
        {
            PendingCount = 0;
        }
    }

    private bool _isLocalScope = true;
    /// <summary>True while the local gallery is displayed.</summary>
    public bool IsLocalScope
    {
        get => _isLocalScope;
        set
        {
            if (SetProperty(ref _isLocalScope, value))
            {
                OnPropertyChanged(nameof(IsSharedScope));
                // U6-D: re-apply the current search text under the new scope.
                if (value)
                    _ = Gallery.RefreshCurrentAsync();
                else
                    SharedGallery?.ApplyFilter(TopBar.SearchText);
            }
        }
    }

    public bool IsSharedScope => !_isLocalScope;

    public RelayCommand SwitchToLocalCommand { get; }
    public RelayCommand SwitchToSharedCommand { get; }

    /// <summary>U6-B: 「提交到共享库」context-menu command; acts on the current primary selection.</summary>
    public AsyncRelayCommand SubmitToSharedCommand { get; }

    /// <summary>
    /// M8-D: 「提交到副库」context-menu command; parameter is the target
    /// <see cref="NasConnectionSpec"/>（副库）。仅管理员可见/可执行（CanSubmitToSecondaryNas）。
    /// 普通用户该命令的菜单项不显示（ShellWindow.xaml 用 IsAdmin 绑定 Visibility）。
    /// </summary>
    public AsyncRelayCommand<NasConnectionSpec?> SubmitToSecondaryNasCommand { get; }

    private async Task LoadSharedGalleryAsync()
    {
        // M8-C: 优先调聚合器（主+副并发加载+去重+注入 SourceNasId）；
        // 聚合器不可用时回退主库 SharedLibrary.LoadAsync（兼容旧调用链）。
        if (SharedGallery is null) return;
        try
        {
            IReadOnlyList<SharedImageRecord> images =
                _services.AggregatedLibrary is { } agg
                    ? await agg.LoadAllAsync(CancellationToken.None)
                    : _services.SharedLibrary is { } lib
                        ? await lib.LoadAsync(CancellationToken.None)
                        : Array.Empty<SharedImageRecord>();
            SharedGallery.Load(images);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SHARED] Load failed: {ex.Message}");
        }
    }

    // ─── PEND-004 (0.18.0): shared collection actions ────────────────────

    private async Task LoadSharedCollectionsAsync()
    {
        if (_services.SharedCollections is not { } service) return;
        string? selectedId = _selectedSharedCollection?.Id;
        try
        {
            var all = await service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            SharedCollectionOptions.Clear();
            SharedCollectionOptions.Add(new SharedCollectionOption(null, "全部共享图片", null));
            foreach (var collection in all)
                SharedCollectionOptions.Add(
                    new SharedCollectionOption(collection.Id, $"{collection.Name}（{collection.ImageIds.Count}）", collection));

            SelectedSharedCollection = SharedCollectionOptions.FirstOrDefault(o => o.Id == selectedId)
                                       ?? SharedCollectionOptions[0];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SHARED] Collections load failed: {ex.Message}");
        }
    }

    private async Task CreateSharedCollectionAsync()
    {
        if (_services.SharedCollections is not { } service) return;
        string? name = RequestInputDialog?.Invoke("新建共享收藏夹", "请输入收藏夹名称：", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var created = await service
                .CreateAsync(name, _services.CurrentUser?.Username ?? string.Empty, CancellationToken.None)
                .ConfigureAwait(true);
            await LoadSharedCollectionsAsync();
            SelectedSharedCollection = SharedCollectionOptions.FirstOrDefault(o => o.Id == created.Id)
                                       ?? SelectedSharedCollection;
            StatusBar.PublishMessage($"已创建共享收藏夹「{created.Name}」");
        }
        catch (InvalidOperationException ex)
        {
            StatusBar.PublishMessage(ex.Message);
        }
        catch (Exception ex)
        {
            StatusBar.PublishMessage($"创建共享收藏夹失败：{ex.Message}");
        }
    }

    private async Task DeleteSharedCollectionAsync()
    {
        if (_services.SharedCollections is not { } service) return;
        if (SelectedSharedCollection is not { Id: { } id, Source: { } collection }) return;

        var result = MessageBox.Show(
            $"确定删除共享收藏夹「{collection.Name}」吗？\n（共享库中的图片不会被删除，仅移除该收藏夹分组）",
            "删除共享收藏夹",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;

        try
        {
            await service.DeleteAsync(id, CancellationToken.None).ConfigureAwait(true);
            SelectedSharedCollection = null;
            await LoadSharedCollectionsAsync();
            StatusBar.PublishMessage("已删除共享收藏夹");
        }
        catch (Exception ex)
        {
            StatusBar.PublishMessage($"删除共享收藏夹失败：{ex.Message}");
        }
    }

    private async Task AddSelectedToSharedCollectionAsync()
    {
        if (_services.SharedCollections is not { } service) return;
        if (SelectedSharedCollection is not { Id: { } id } selected || SharedGallery is not { } gallery) return;

        var hashes = gallery.SelectedItems
            .Select(i => i.FileHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0)
        {
            StatusBar.PublishMessage("请先在共享库中选中图片");
            return;
        }

        try
        {
            int added = await service.AddImagesAsync(id, hashes, CancellationToken.None).ConfigureAwait(true);
            await LoadSharedCollectionsAsync();
            StatusBar.PublishMessage(added > 0
                ? $"已加入共享收藏夹「{selected.Source!.Name}」{added} 张"
                : "所选图片已在共享收藏夹中");
        }
        catch (Exception ex)
        {
            StatusBar.PublishMessage($"加入共享收藏夹失败：{ex.Message}");
        }
    }

    private async Task RemoveSelectedFromSharedCollectionAsync()
    {
        if (_services.SharedCollections is not { } service) return;
        if (SelectedSharedCollection is not { Id: { } id } selected || SharedGallery is not { } gallery) return;

        var hashes = gallery.SelectedItems
            .Select(i => i.FileHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0)
        {
            StatusBar.PublishMessage("请先在共享库中选中图片");
            return;
        }

        try
        {
            int removed = await service.RemoveImagesAsync(id, hashes, CancellationToken.None).ConfigureAwait(true);
            await LoadSharedCollectionsAsync();
            StatusBar.PublishMessage(removed > 0
                ? $"已从「{selected.Source!.Name}」移出 {removed} 张"
                : "所选图片不在该共享收藏夹中");
        }
        catch (Exception ex)
        {
            StatusBar.PublishMessage($"移出共享收藏夹失败：{ex.Message}");
        }
    }

    /// <summary>U6-B/F-11: whether the current primary selection can be submitted to the shared library.</summary>
    public bool CanSubmitToShared => Gallery.PrimarySelected?.CanSubmitToShared == true;

    /// <summary>U6-B: uploads the current primary selection and reports the outcome.</summary>
    private async Task SubmitToSharedAsync()
    {
        var item = Gallery.PrimarySelected;
        if (item is null ||
            _services.Submissions is not { } submissions ||
            _services.CurrentUser is not { } user)
        {
            return;
        }

        var result = await submissions
            .SubmitFromLocalAsync(item, user.Username, CancellationToken.None)
            .ConfigureAwait(true);

        switch (result.Outcome)
        {
            case SubmissionOutcome.Submitted:
                item.CanSubmitToShared = false;
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "已提交到共享库",
                    $"「{item.FileName}」已上传，等待管理员审核");
                break;
            case SubmissionOutcome.DuplicateInLibrary:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "无需重复提交",
                    $"「{item.FileName}」已存在于共享库");
                break;
            case SubmissionOutcome.DuplicatePending:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "正在审核中",
                    $"「{item.FileName}」已提交，正在等待管理员审核");
                break;
            case SubmissionOutcome.MissingLocalFile:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "提交失败",
                    $"「{item.FileName}」的本地原文件不存在或不可访问");
                break;
            case SubmissionOutcome.NotPermitted:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "权限不足",
                    "仅管理员可向副库提交数据");
                break;
        }
    }

    /// <summary>
    /// M8-D: 提交到副库。仅管理员可执行（CanSubmitToSecondaryNas）。
    /// 从 NasRegistry 取副库 SharedStorageService，构造临时 SubmissionService
    /// 调 SubmitFromLocalAsync。非 admin 由 CanExecute 拦截（菜单项不显示），
    /// 但方法内仍 defensive 校验权限以防绕过。
    /// </summary>
    private async Task SubmitToSecondaryNasAsync(NasConnectionSpec? spec)
    {
        if (spec is null) return;
        var item = Gallery.PrimarySelected;
        if (item is null || _services.CurrentUser is not { } user) return;

        // Defensive 权限校验（CanExecute 已拦，但防绕过）
        if (!_services.Permissions.CanSubmitToSecondaryNas)
        {
            _services.Notifications.Publish(NotificationKind.SharedSubmission,
                "权限不足",
                "仅管理员可向副库提交数据");
            return;
        }

        var entry = _services.NasRegistry?.GetSecondary(spec.Id);
        if (entry is null)
        {
            _services.Notifications.Publish(NotificationKind.SharedSubmission,
                "副库不可用",
                $"目标副库「{spec.Name}」未连接或已下线");
            return;
        }

        // 构造临时 SubmissionService 绑定副库 storage
        var storage = entry.Value.Storage;
        var library = new SharedLibraryService(storage);
        var submissions = new SubmissionService(storage, library,
            _services.Thumbnails, _services.Settings);

        var result = await submissions
            .SubmitFromLocalAsync(item, user.Username, CancellationToken.None)
            .ConfigureAwait(true);

        switch (result.Outcome)
        {
            case SubmissionOutcome.Submitted:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "已提交到副库",
                    $"「{item.FileName}」已上传到副库「{spec.Name}」，等待管理员审核");
                break;
            case SubmissionOutcome.DuplicateInLibrary:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "无需重复提交",
                    $"「{item.FileName}」已存在于副库「{spec.Name}」");
                break;
            case SubmissionOutcome.DuplicatePending:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "正在审核中",
                    $"「{item.FileName}」已提交到副库「{spec.Name}」，正在等待审核");
                break;
            case SubmissionOutcome.MissingLocalFile:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "提交失败",
                    $"「{item.FileName}」的本地原文件不存在或不可访问");
                break;
            case SubmissionOutcome.NotPermitted:
                _services.Notifications.Publish(NotificationKind.SharedSubmission,
                    "权限不足",
                    "仅管理员可向副库提交数据");
                break;
        }
    }

    public RelayCommand FocusSearchCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    /// <summary>M5-G: opens the persistent download queue window.</summary>
    public RelayCommand ShowDownloadQueueCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    /// <summary>
    /// N7: scan a NAS library. Opens a connection picker (or skips straight
    /// to the scan when only one connection is configured), runs
    /// <see cref="RemoteLibraryScanner"/> with progress on the status bar,
    /// then refreshes the gallery so the new remote rows appear.
    /// </summary>
    public AsyncRelayCommand ScanNasLibraryCommand { get; }

    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand OpenItemCommand { get; }
    public RelayCommand RevealItemCommand { get; }
    public RelayCommand CopyItemPathCommand { get; }
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

    /// <summary>U6-D: App wires this to show SaveFileDialog for 「保存副本」. Returns null on cancel.</summary>
    public event Func<string, string?>? RequestSaveCopyDialog;

    /// <summary>App wires this to show the W3 settings window.</summary>
    public event Action? ShowSettingsDialog;

    /// <summary>App wires this to show the M5-G download queue window.</summary>
    public event Action? ShowDownloadQueueDialog;

    public string Title => $"{AppInfo.ProductName} — {AppInfo.ProductSubtitle}";
    public string VersionText => $"v{AppInfo.Version} · {AppInfo.MilestoneLabel}";
    public string AiStatusText => "AI：关闭";

    /// <summary>
    /// W1/C1-NAME (v0.12.3): 底部状态栏在 AI 档位之后追加显示
    /// 「{EnglishName} v{Version}」。顶栏 W1/C1-VER 保留品牌化
    /// 「vX.Y.Z · 里程碑标签」展示，本属性仅用于底部简洁署名。
    /// </summary>
    public string AppVersionText => $"{AppInfo.EnglishName} v{AppInfo.Version}";

    // ─── U3-C (§4.E.3): B1-COL collapsible sidebar sections ────────────────
    // Expanded state persists; collapsed headers show the child count as (N).

    public bool SidebarLibraryExpanded
    {
        get => _services.Settings.SidebarLibraryExpanded;
        set
        {
            if (_services.Settings.SidebarLibraryExpanded == value) return;
            _services.Settings.SidebarLibraryExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LibraryHeaderText));
        }
    }

    public bool SidebarCollectionsExpanded
    {
        get => _services.Settings.SidebarCollectionsExpanded;
        set
        {
            if (_services.Settings.SidebarCollectionsExpanded == value) return;
            _services.Settings.SidebarCollectionsExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollectionsHeaderText));
        }
    }

    public bool SidebarTopTagsExpanded
    {
        get => _services.Settings.SidebarTopTagsExpanded;
        set
        {
            if (_services.Settings.SidebarTopTagsExpanded == value) return;
            _services.Settings.SidebarTopTagsExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TopTagsHeaderText));
        }
    }

    public bool SidebarPbrGroupsExpanded
    {
        get => _services.Settings.SidebarPbrGroupsExpanded;
        set
        {
            if (_services.Settings.SidebarPbrGroupsExpanded == value) return;
            _services.Settings.SidebarPbrGroupsExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PbrGroupsHeaderText));
        }
    }

    // ─── v0.13.0: B1 图标模式 / B3 详情折叠 ─────────────────────────────

    /// <summary>
    /// true=B1 左栏图标模式（约 60px，仅分类图标）；
    /// false=图标+文字模式（约 240px，完整分类树）。
    /// </summary>
    public bool IsSidebarIconMode
    {
        get => _services.Settings.SidebarIconMode;
        set
        {
            if (_services.Settings.SidebarIconMode == value) return;
            _services.Settings.SidebarIconMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSidebarTextMode));
        }
    }

    /// <summary>IsSidebarIconMode 的反向绑定（XAML 中便于表达「文字模式」显隐）。</summary>
    public bool IsSidebarTextMode => !IsSidebarIconMode;

    /// <summary>
    /// true=B3 详情栏展开（约 300px 完整面板）；
    /// false=折叠（约 40px 窄条）。
    /// </summary>
    public bool IsDetailPanelExpanded
    {
        get => _services.Settings.DetailPanelExpanded;
        set
        {
            if (_services.Settings.DetailPanelExpanded == value) return;
            _services.Settings.DetailPanelExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDetailPanelCollapsed));
        }
    }

    /// <summary>IsDetailPanelExpanded 的反向绑定。</summary>
    public bool IsDetailPanelCollapsed => !IsDetailPanelExpanded;

    /// <summary>切换 B1 左栏图标/文字模式。</summary>
    public RelayCommand ToggleSidebarModeCommand => new(() => IsSidebarIconMode = !IsSidebarIconMode);

    /// <summary>切换 B3 详情栏展开/折叠。</summary>
    public RelayCommand ToggleDetailPanelCommand => new(() => IsDetailPanelExpanded = !IsDetailPanelExpanded);

    public string LibraryHeaderText =>
        SidebarLibraryExpanded ? "素材库" : $"素材库 ({TagTree.CategoryNodes.Count})";

    public string CollectionsHeaderText =>
        SidebarCollectionsExpanded ? "收藏夹" : $"收藏夹 ({CollectionTree.CollectionNodes.Count})";

    public string TopTagsHeaderText =>
        SidebarTopTagsExpanded ? "热门标签（Top 50）" : $"热门标签（Top 50） ({TagTree.TopTagNodes.Count})";

    public string PbrGroupsHeaderText =>
        SidebarPbrGroupsExpanded ? "贴图组" : $"贴图组 ({PbrGroupTree.GroupNodes.Count})";

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

    /// <summary>
    /// A1-10 主动刷新（v0.8.0）：逐个重扫全部被监控目录（ImportService 按
    /// hash 幂等去重，已入库文件跳过）→ 重载画廊 → 状态栏汇报。无监控目录
    /// 或目录不存在时退化为纯画廊重载（与旧 F5 行为一致）。复用 IsImporting
    /// 防跨命令重入（期间导入/扫描 NAS/AI/颜色按钮同步禁用）；命令自身
    /// AsyncRelayCommand._isExecuting 防按钮连点。F5 快捷键绑定同一命令。
    /// </summary>
    private async Task RefreshLibraryAsync()
    {
        if (TopBar.IsImporting) return;
        TopBar.IsImporting = true;
        RefreshCommand.RaiseCanExecuteChanged();
        try
        {
            int scanned = 0, added = 0;
            IReadOnlyList<string> watched = _services.Settings.WatcherDirectories;
            if (watched.Count > 0)
            {
                var progress = new Progress<ImportProgress>(p =>
                    StatusBar.PublishMessage(
                        $"刷新：{p.Processed}/{p.ScannedTotal}（新增 {p.Added}）{Path.GetFileName(p.CurrentFile)}"));
                foreach (string dir in watched)
                {
                    if (!Directory.Exists(dir)) continue;
                    var options = new ImportOptions
                    {
                        InputPaths = [dir],
                        RecurseSubfolders = true,
                        SkipHidden = true,
                        EnabledExtensions = ImportOptions.SupportedExtensions,
                    };
                    ImportReport report = await _services.Import.ImportAsync(
                        options, progress, CancellationToken.None);
                    scanned += report.ScannedTotal;
                    added += report.AddedCount;
                }
            }

            await Gallery.RefreshAsync();
            if (added > 0)
            {
                RunColorBackfill();
                StatusBar.PublishMessage($"刷新完成：扫描 {scanned}，新增 {added} 张。");
            }
            else
            {
                StatusBar.PublishMessage(watched.Count > 0
                    ? $"刷新完成：扫描 {scanned} 个文件，无新增。"
                    : "刷新完成（未配置监控目录，仅重载画廊）。");
            }
        }
        finally
        {
            TopBar.IsImporting = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    // ─── Import ──────────────────────────────────────────────────────────────

    private async Task ImportAsync()
    {
        if (TopBar.IsImporting) return;
        TopBar.IsImporting = true;
        try
        {
            var vm = new ImportViewModel(_services.Import, GetSuggestedImportFolder,
                Settings.AiBackends, _services.Notifications);
            // v0.12.0: 导入完成后按所选后端自动分析本次新增图。
            vm.ImportAnalysisRequested +=
                (ids, backendId) => _ = RunImportAiAnalysisAsync(ids, backendId);
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

    /// <summary>
    /// v0.12.0: 导入完成后按所选后端 id 对本次新增图执行作用域 AI 分析。
    /// 进度经 AiAnalysisService.BatchProgress 事件反映到底部信息提示栏；
    /// 完成/取消通知由服务发布（v0.8.1 节流收敛语义）。
    /// </summary>
    private async Task RunImportAiAnalysisAsync(IReadOnlyList<long> imageIds, int backendId)
    {
        if (imageIds.Count == 0) return;
        try
        {
            await _services.AiAnalysis.RunBatchAsync(
                onlyImageIds: imageIds, backendIdOverride: backendId);
            await Gallery.RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // 服务已发布取消通知。
        }
        catch (Exception ex)
        {
            StatusBar.PublishMessage($"AI 分析失败：{ex.Message}");
        }
    }

    // ─── NAS library scan (N7) ────────────────────────────────────────────

    private async Task ScanNasLibraryAsync()
    {
        IReadOnlyList<NasConnectionSpec> connections = _services.Settings.NasConnections;
        if (connections.Count == 0)
        {
            StatusBar.PublishMessage("请先在设置 → NAS 中配置一个连接。");
            ShowSettingsDialog?.Invoke();
            return;
        }

        NasConnectionSpec? picked = connections.Count == 1
            ? connections[0]
            : await PickNasConnectionAsync(connections);
        if (picked is null)
            return;

        TopBar.IsImporting = true;
        ScanNasLibraryCommand.RaiseCanExecuteChanged();
        try
        {
            var scanner = new RemoteLibraryScanner(_services.Import, _services.NasFactory, _services.Settings);
            var progress = new Progress<ImportProgress>(p =>
                StatusBar.PublishMessage(
                    $"扫描 NAS {picked.Name}：{p.Processed}/{p.ScannedTotal}（新增 {p.Added}）{Path.GetFileName(p.CurrentFile)}"));
            StatusBar.PublishMessage($"正在扫描 NAS 库 {picked.Name}…");

            ImportReport report = await scanner.ScanAsync(picked.Id, progress, CancellationToken.None);
            await Gallery.RefreshAsync();

            if (report.Cancelled)
                StatusBar.PublishMessage($"已取消扫描 NAS {picked.Name}：扫描 {report.ScannedTotal}，导入 {report.AddedCount}。");
            else
                StatusBar.PublishMessage($"扫描 NAS {picked.Name} 完成：扫描 {report.ScannedTotal}，导入 {report.AddedCount}，跳过 {report.SkippedCount}。");
            RunColorBackfill();
        }
        catch (NasConnectionException ex)
        {
            StatusBar.PublishMessage($"扫描 NAS 失败（{ex.Kind}）：{ex.Message}");
        }
        catch (Exception ex)
        {
            StatusBar.PublishMessage($"扫描 NAS 失败：{ex.Message}");
        }
        finally
        {
            TopBar.IsImporting = false;
            ScanNasLibraryCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Minimal connection picker built in code (no new XAML) for the
    /// &gt;1-connection case. Returns null when the user cancels.
    /// </summary>
    private static async Task<NasConnectionSpec?> PickNasConnectionAsync(
        IReadOnlyList<NasConnectionSpec> connections)
    {
        var dialog = new Window
        {
            Title = "选择要扫描的 NAS 连接",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanMinimize,
        };

        var combo = new System.Windows.Controls.ComboBox
        {
            Margin = new Thickness(16, 16, 16, 8),
            ItemsSource = connections.ToList(),
            DisplayMemberPath = "Name",
            SelectedIndex = 0,
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "扫描",
            Margin = new Thickness(16, 0, 8, 16),
            Padding = new Thickness(16, 4, 16, 4),
            IsDefault = true,
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            Margin = new Thickness(8, 0, 16, 16),
            Padding = new Thickness(16, 4, 16, 4),
            IsCancel = true,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Children = { ok, cancel },
        };
        dialog.Content = new System.Windows.Controls.StackPanel
        {
            Children = { combo, buttons },
        };

        bool? result = await Application.Current.Dispatcher.InvokeAsync(() => dialog.ShowDialog());
        return result == true ? (NasConnectionSpec?)combo.SelectedItem : null;
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

    // ─── U3-B: hover card actions (parameter = the hovered GalleryItemViewModel) ──

    private static bool CanActOnItem(object? parameter) =>
        parameter is GalleryItemViewModel { IsMissing: false };

    private static void OpenItem(object? parameter)
    {
        if (parameter is GalleryItemViewModel { IsMissing: false } item)
            SystemNavigator.OpenFile(item.FilePath);
    }

    private static void RevealItem(object? parameter)
    {
        if (parameter is GalleryItemViewModel { IsMissing: false } item)
            SystemNavigator.RevealFile(item.FilePath);
    }

    private static void CopyItemPath(object? parameter)
    {
        if (parameter is GalleryItemViewModel { IsMissing: false } item)
            Clipboard.SetText(item.FilePath);
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
            ThumbnailProvider.Regenerate(item);
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
        UpdateSubmitFlags();
        _ = TagTree.RefreshCountsAsync();
        _ = CollectionTree.RefreshAsync();
        _ = PbrGroupTree.RefreshAsync();
    }

    /// <summary>
    /// U6-B: marks local-file rows eligible for 「提交到共享库」. Runs after
    /// every gallery refresh (initial load, import, re-filter).
    /// </summary>
    private void UpdateSubmitFlags()
    {
        bool available = IsSharedLibraryAvailable;
        foreach (var item in Gallery.Items)
        {
            item.CanSubmitToShared =
                available &&
                item.Source == ImageSource.Local &&
                !string.IsNullOrEmpty(item.FilePath) &&
                File.Exists(item.FilePath);
        }
    }

    private void OnSelectionChanged()
    {
        StatusBar.UpdateShortcutHint(Gallery.SelectedItems.Count, Gallery.TotalCount);
        // F-11: keep the gallery context menu's submit item in sync with the selection.
        OnPropertyChanged(nameof(CanSubmitToShared));
        SubmitToSharedCommand.RaiseCanExecuteChanged();
    }

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
        // U2-B: 清除全部筛选 also resets the 最近入库 dropdown.
        Gallery.Filter.ImportedWithinDays = null;
        // U3-A: reset the three attribute filter dimensions.
        Gallery.Filter.ResolutionBucket = null;
        Gallery.Filter.FileSizeBucket = null;
        Gallery.Filter.RatingBucket = null;
        // M12-F: reset B1-6 筛选标签组 dimensions.
        Gallery.Filter.ArtStyleTags = null;
        Gallery.Filter.CompositionTags = null;
        Gallery.Filter.LightingTags = null;
        Gallery.Filter.ThemeTags = null;
        Gallery.Filter.ConditionTags = null;
        Gallery.Filter.TextureTags = null; // v0.13.7: 质感（B1-6-11）
        Gallery.Filter.FileFormats = null;
        Gallery.Filter.ColorPaletteHexes = null;
        TopBar.ClearSearchText();
        TopBar.ClearRecentImport();
        TagTree.ResetSelection();
        CollectionTree.ResetSelection();
        PbrGroupTree.ResetSelection();
        FilterGroups.ResetSelection();
        _ = Gallery.RefreshAsync();
    }

    /// <summary>
    /// W1/B2-3-CM1 图卡右键「找相似图」/ W1/B3-8 详情栏「找相似」按钮。
    /// 调 SemanticSearchService.SearchByImageId 获取 top-50 相似图片 id，
    /// 设置 Gallery.Filter 进入语义模式并刷新画廊。
    /// </summary>
    private async Task FindSimilarAsync(long? imageId = null)
    {
        GalleryItemViewModel? primary = imageId is long id
            ? Gallery.Items.FirstOrDefault(i => i.Id == id)
            : Gallery.PrimarySelected;
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

/// <summary>
/// PEND-004 (0.18.0): one entry in the shared-collection picker. A null
/// <paramref name="Id"/> is the "all images" sentinel; <paramref name="Source"/>
/// keeps the loaded record so the filter can read its image hashes.
/// </summary>
public sealed record SharedCollectionOption(string? Id, string Display, SharedCollection? Source);
