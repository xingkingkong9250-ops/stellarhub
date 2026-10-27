using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;

namespace QL.World.ViewModels;

/// <summary>0.16.0 DUP-D: W8 工具栏筛选下拉项。</summary>
public sealed record DuplicateFilterOption(string Display, object Value);

/// <summary>
/// 0.16.0 DUP-D: W8 重复审核窗口的 ViewModel（纯 MVVM）。
/// 负责：全库扫描（后台 + 进度 + 可取消）、类型/状态筛选、组列表与组详情、
/// 「保留全部」（组 → dismissed）、「删除选中」（复用 ImageQueryService 移除
/// + 会话内撤销；组内只剩 1 张时自动 → resolved）。
/// </summary>
public sealed class DuplicateReviewViewModel : ObservableObject, IDisposable
{
    private const int UNDO_WINDOW_SECONDS = 8;

    private readonly DuplicateReviewService _review;
    private readonly DuplicateScanService _scanner;
    private readonly ImageQueryService? _query;
    private readonly ThumbnailService? _thumbnails;
    private readonly DispatcherTimer _undoTimer;
    private readonly Dictionary<string, List<DuplicateMemberViewModel>> _thumbWaiters = [];
    private readonly object _thumbGate = new();
    // 筛选切换会并发触发重载；串行化避免两次 LoadAsync 交错清空集合。
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private CancellationTokenSource? _scanCts;
    private RemovedEntryBundle? _lastRemoved;
    private bool _isScanning;
    private double _scanPercent;
    private string _scanPhaseText = string.Empty;
    private string _scanDetailText = string.Empty;
    private string _statusText = string.Empty;
    private bool _undoVisible;
    private string _undoText = string.Empty;
    private DuplicateGroupViewModel? _selectedGroup;
    private DuplicateKindFilter _selectedKindFilter = DuplicateKindFilter.All;
    private DuplicateStatusFilter _selectedStatusFilter = DuplicateStatusFilter.Pending;
    private bool _disposed;

    public DuplicateReviewViewModel(
        DuplicateReviewService review,
        DuplicateScanService scanner,
        ImageQueryService? query = null,
        ThumbnailService? thumbnails = null)
    {
        _review = review ?? throw new ArgumentNullException(nameof(review));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _query = query;
        _thumbnails = thumbnails;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !_isScanning);
        CancelScanCommand = new RelayCommand(CancelScan, () => _isScanning);
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !_isScanning);
        KeepAllCommand = new RelayCommand(
            parameter => _ = KeepAllAsync(parameter as DuplicateGroupViewModel),
            parameter => parameter is DuplicateGroupViewModel { IsPending: true } && !_isScanning);
        DeleteSelectedCommand = new RelayCommand(
            parameter => _ = DeleteSelectedAsync(parameter as DuplicateGroupViewModel),
            parameter => _query is not null &&
                parameter is DuplicateGroupViewModel { IsPending: true } group &&
                group.HasSelection &&
                !_isScanning);
        UndoCommand = new RelayCommand(Undo, () => _lastRemoved is not null);

        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(UNDO_WINDOW_SECONDS) };
        _undoTimer.Tick += (_, _) => HideUndoBar();

        if (_thumbnails is not null)
            _thumbnails.ThumbnailReady += OnThumbnailReady;
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    public IReadOnlyList<DuplicateFilterOption> KindOptions { get; } =
    [
        new("全部类型", DuplicateKindFilter.All),
        new("严格同图", DuplicateKindFilter.Exact),
        new("近似变体", DuplicateKindFilter.Variant),
    ];

    public IReadOnlyList<DuplicateFilterOption> StatusOptions { get; } =
    [
        new("待处理", DuplicateStatusFilter.Pending),
        new("已处理", DuplicateStatusFilter.Processed),
    ];

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand KeepAllCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand UndoCommand { get; }

    public DuplicateKindFilter SelectedKindFilter
    {
        get => _selectedKindFilter;
        set
        {
            if (SetProperty(ref _selectedKindFilter, value))
                _ = LoadAsync();
        }
    }

    public DuplicateStatusFilter SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            if (SetProperty(ref _selectedStatusFilter, value))
                _ = LoadAsync();
        }
    }

    public DuplicateGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
                DeleteSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value))
                return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            KeepAllCommand.RaiseCanExecuteChanged();
            DeleteSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public double ScanPercent
    {
        get => _scanPercent;
        private set => SetProperty(ref _scanPercent, value);
    }

    public string ScanPhaseText
    {
        get => _scanPhaseText;
        private set => SetProperty(ref _scanPhaseText, value);
    }

    public string ScanDetailText
    {
        get => _scanDetailText;
        private set => SetProperty(ref _scanDetailText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

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

    /// <summary>按当前筛选重载组列表，并保留原选中组（若仍存在）。</summary>
    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            string? previousGroupId = SelectedGroup?.GroupId;
            List<DuplicateGroupRecord> records = await Task.Run(
                () => _review.ListGroups(SelectedKindFilter, SelectedStatusFilter));

            Groups.Clear();
            foreach (DuplicateGroupRecord record in records)
            {
                var group = new DuplicateGroupViewModel(record);
                foreach (DuplicateMemberViewModel member in group.Members)
                {
                    member.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(DuplicateMemberViewModel.IsSelected))
                            DeleteSelectedCommand.RaiseCanExecuteChanged();
                    };
                    AttachThumbnail(member);
                }
                Groups.Add(group);
            }

            SelectedGroup = Groups.FirstOrDefault(group => group.GroupId == previousGroupId)
                ?? Groups.FirstOrDefault();
            OnPropertyChanged(nameof(HasGroups));
            OnPropertyChanged(nameof(GroupsSummaryText));
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public bool HasGroups => Groups.Count > 0;

    public string GroupsSummaryText => $"共 {Groups.Count} 组";

    /// <summary>后台执行全库扫描；完成后按当前筛选重载。</summary>
    public async Task ScanAsync()
    {
        if (_isScanning)
            return;

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanPercent = 0;
        ScanPhaseText = "准备扫描…";
        ScanDetailText = string.Empty;
        StatusText = string.Empty;

        try
        {
            var progress = new Progress<DuplicateScanProgress>(OnScanProgress);
            DuplicateScanResult result = await _scanner
                .ScanAsync(progress, _scanCts.Token);

            StatusText = result.Cancelled
                ? "扫描已取消（未写入候选）"
                : $"扫描完成：{result.GroupCount} 组"
                  + $"（同图 {result.ExactGroupCount} / 变体 {result.VariantGroupCount}）"
                  + (result.L3PairsReviewed > 0
                      ? $"，L3 终审确认 {result.L3ConfirmedPairs}/{result.L3PairsReviewed} 对"
                      : string.Empty);
            ScanPhaseText = result.Cancelled ? "已取消" : "完成";
            ScanPercent = result.Cancelled ? ScanPercent : 100;
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            IsScanning = false;
            await LoadAsync();
        }
    }

    public void CancelScan() => _scanCts?.Cancel();

    /// <summary>保留整组：组内候选行 → dismissed，从待处理列表移除。</summary>
    public async Task KeepAllAsync(DuplicateGroupViewModel? group)
    {
        if (group is null || !group.IsPending)
            return;

        _review.SetGroupStatus(group.GroupId, "dismissed");
        StatusText = $"已保留整组 {group.MemberCount} 张（不再提示）";
        await LoadAsync();
    }

    /// <summary>
    /// 删除组内勾选的图片：走 ImageQueryService.Remove（记录级移除，不删磁盘，
    /// 8 秒内可撤销）。删除后组内只剩 1 张时整组 → resolved。
    /// </summary>
    public async Task<int> DeleteSelectedAsync(DuplicateGroupViewModel? group)
    {
        if (group is null || _query is null)
            return 0;

        List<long> ids = group.Members
            .Where(member => member.IsSelected)
            .Select(member => member.ImageId)
            .ToList();
        if (ids.Count == 0)
            return 0;

        _lastRemoved = _query.Remove(ids);

        // Remove 已清掉被删图片的候选行；若该组仍有候选则按剩余数判定。
        DuplicateGroupRecord? remaining = _review
            .ListGroups(DuplicateKindFilter.All, DuplicateStatusFilter.Pending)
            .FirstOrDefault(candidate => candidate.GroupId == group.GroupId);
        if (remaining is { Members.Count: 1 })
            _review.SetGroupStatus(group.GroupId, "resolved");

        ShowUndoBar(ids.Count);
        StatusText = remaining is { Members.Count: 1 }
            ? $"已移除 {ids.Count} 张，组内仅剩 1 张（组已标记为已处理）"
            : $"已移除 {ids.Count} 张";
        UndoCommand.RaiseCanExecuteChanged();
        await LoadAsync();
        return ids.Count;
    }

    /// <summary>撤销上一次删除（恢复图片行；候选行需下次扫描重建）。</summary>
    public void Undo()
    {
        if (_lastRemoved is null || _query is null)
            return;

        int restored = _query.Restore(_lastRemoved);
        _lastRemoved = null;
        HideUndoBar();
        StatusText = restored > 0 ? $"已恢复 {restored} 张图片" : "没有可恢复的图片（路径可能已冲突）";
        UndoCommand.RaiseCanExecuteChanged();
        _ = LoadAsync();
    }

    // ── 缩略图 ────────────────────────────────────────────────────────

    /// <summary>
    /// 已有缩略图直接后台解码；本地行缺失时请求生成，远程行只显示已缓存
    /// 缩略图（避免在 W8 触发 NAS 整图下载）。
    /// </summary>
    private void AttachThumbnail(DuplicateMemberViewModel member)
    {
        if (_thumbnails is null)
            return;

        if (_thumbnails.ThumbnailExists(member.FileHash, ThumbnailLevel.Thumb))
        {
            _ = LoadThumbnailAsync(member);
            return;
        }

        if (member.Source != ImageSource.Local)
            return;

        lock (_thumbGate)
        {
            if (!_thumbWaiters.TryGetValue(member.FileHash, out List<DuplicateMemberViewModel>? waiters))
            {
                waiters = [];
                _thumbWaiters[member.FileHash] = waiters;
            }
            waiters.Add(member);
        }
        _thumbnails.Request(
            member.FileHash, new LocalImageSource(member.FilePath),
            ThumbnailLevel.Thumb, highPriority: false);
    }

    private async Task LoadThumbnailAsync(DuplicateMemberViewModel member)
    {
        if (_thumbnails is null)
            return;

        string path = _thumbnails.GetThumbnailPath(member.FileHash, ThumbnailLevel.Thumb);
        BitmapSource? bitmap = await Task.Run(() => Helpers.WebpImageDecoder.DecodeFile(path));
        if (bitmap is not null)
            member.ThumbnailImage = bitmap;
    }

    private void OnThumbnailReady(string hash, ThumbnailLevel level)
    {
        if (_disposed || level != ThumbnailLevel.Thumb || _thumbnails is null)
            return;

        List<DuplicateMemberViewModel> targets;
        lock (_thumbGate)
        {
            if (!_thumbWaiters.Remove(hash, out List<DuplicateMemberViewModel>? waiters))
                return;
            targets = waiters;
        }

        _ = PublishThumbnailsAsync(hash, targets);
    }

    private async Task PublishThumbnailsAsync(
        string hash, List<DuplicateMemberViewModel> targets)
    {
        if (_thumbnails is null)
            return;

        string path = _thumbnails.GetThumbnailPath(hash, ThumbnailLevel.Thumb);
        BitmapSource? bitmap = await Task.Run(() => Helpers.WebpImageDecoder.DecodeFile(path));
        if (bitmap is null)
            return;

        foreach (DuplicateMemberViewModel member in targets)
            member.ThumbnailImage = bitmap;
    }

    // ── 进度与撤销条 ──────────────────────────────────────────────────

    private void OnScanProgress(DuplicateScanProgress progress)
    {
        ScanPhaseText = progress.Phase switch
        {
            DuplicateScanPhase.Backfill => "补算感知哈希",
            DuplicateScanPhase.PhashPairing => "严格同图配对（L1）",
            DuplicateScanPhase.VectorScan => "近似变体扫描（L2）",
            DuplicateScanPhase.TerminalReview => "VLM 终审（L3）",
            DuplicateScanPhase.Persist => "写入候选组",
            _ => "扫描中",
        };
        ScanDetailText = progress.Total > 0
            ? $"{progress.Processed}/{progress.Total}"
            : string.Empty;
        ScanPercent = progress.Total > 0
            ? Math.Clamp(progress.Processed * 100.0 / progress.Total, 0, 100)
            : 0;
    }

    private void ShowUndoBar(int count)
    {
        UndoText = $"已移除 {count} 张，{UNDO_WINDOW_SECONDS} 秒内可撤销";
        UndoVisible = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void HideUndoBar()
    {
        _undoTimer.Stop();
        UndoVisible = false;
        UndoText = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _undoTimer.Stop();
        if (_thumbnails is not null)
            _thumbnails.ThumbnailReady -= OnThumbnailReady;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _loadGate.Dispose();
    }
}
