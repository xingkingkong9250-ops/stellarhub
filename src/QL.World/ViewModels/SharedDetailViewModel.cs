using System.Windows;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Nas;

namespace QL.World.ViewModels;

/// <summary>
/// U6-D: read-only detail pane for a shared-library image. Mirrors the
/// display fields of <see cref="DetailViewModel"/> without any edit surface,
/// plus 「保存副本到…」 (download original) and 「复制 NAS 路径」.
/// </summary>
public sealed class SharedDetailViewModel : ObservableObject
{
    private readonly SharedGalleryViewModel _gallery;
    private readonly NasConnectionRegistry _registry;

    private bool _hasSelection;
    private int _selectedCount;
    private GalleryItemViewModel? _item;
    private SharedImageRecord? _record;

    public SharedDetailViewModel(
        SharedGalleryViewModel gallery,
        NasConnectionRegistry registry)
    {
        _gallery = gallery ?? throw new ArgumentNullException(nameof(gallery));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        SaveCopyCommand = new AsyncRelayCommand(SaveCopyAsync);
        CopyNasPathCommand = new RelayCommand(CopyNasPath);

        gallery.SelectionChanged += OnSelectionChanged;
    }

    // ─── State ───────────────────────────────────────────────────────────

    public bool HasSelection
    {
        get => _hasSelection;
        private set
        {
            if (SetProperty(ref _hasSelection, value))
                OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    public bool ShowEmpty => !_hasSelection;

    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetProperty(ref _selectedCount, value);
    }

    public GalleryItemViewModel? Preview => _item;
    public string? FileName => _item?.FileName;
    public string? DimensionsText => _item?.DimensionsText;
    public string? FileSizeText => _item?.FileSizeText;
    public string? FormatText => _item?.Format?.ToUpperInvariant();
    public int Rating => _item?.Rating ?? 0;
    public string? CategoryText
        => string.IsNullOrEmpty(_item?.Category) ? "未分类" : _item!.Category;
    public string? TagsSummary
        => string.IsNullOrEmpty(_item?.TagsCsv) ? "无标签" : _item!.TagsCsv;

    public string? SubmittedBy => _record?.SubmittedBy;
    public string? ApprovedBy => _record?.ApprovedBy;
    public string? ApprovedAtText =>
        _record?.ApprovedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>NAS path of the original; null when it never reached the shard.</summary>
    public string? NasPath => _record?.SourceNasPath;

    // ─── Commands ─────────────────────────────────────────────────────────

    public AsyncRelayCommand SaveCopyCommand { get; }
    public RelayCommand CopyNasPathCommand { get; }

    /// <summary>App supplies the SaveFileDialog via this hook (default name → target path).</summary>
    public event Func<string, string?>? RequestSaveFile;

    /// <summary>Raised after a save attempt so the shell publishes a notification.</summary>
    public event Action<NotificationKind, string, string>? SaveCompleted;

    private async Task SaveCopyAsync()
    {
        if (_item is null || NasPath is not { } nasPath) return;

        // M8-C: 按 _record.SourceNasId 路由到对应 NAS connector（主或副）。
        // SourceNasId 为 null 时（旧 library.json 主库记录）回退主 NAS。
        INasConnector? connector = ResolveConnector(_record?.SourceNasId);
        if (connector is null)
        {
            SaveCompleted?.Invoke(NotificationKind.SharedSubmission,
                "保存副本失败",
                "无法解析数据来源 NAS，可能已下线或被移除。");
            return;
        }

        string? target = RequestSaveFile?.Invoke(_item.FileName);
        if (string.IsNullOrEmpty(target)) return;

        try
        {
            await connector.DownloadFileAsync(nasPath, target, CancellationToken.None);
            SaveCompleted?.Invoke(NotificationKind.SharedSubmission,
                "已保存副本",
                $"「{_item.FileName}」已保存到：{target}");
        }
        catch (NasConnectionException ex)
        {
            SaveCompleted?.Invoke(NotificationKind.SharedSubmission,
                "保存副本失败",
                $"无法下载原图：{ex.Message}");
        }
    }

    /// <summary>
    /// M8-C: 按记录的 SourceNasId 从 NasConnectionRegistry 取对应 connector。
    /// id 为副库 → GetSecondary(id)；id 为主库或 null → GetPrimary。
    /// id 不在缓存（库下线/被删）→ 回退主 NAS，让下载尝试后失败而非直接报错。
    /// </summary>
    private INasConnector? ResolveConnector(int? sourceNasId)
    {
        if (sourceNasId is int id)
        {
            var sec = _registry.GetSecondary(id);
            if (sec is { } s) return s.Connector;
            // 可能 id 是主库（GetSecondary 对主库返回 null）或副库已下线
            var pri = _registry.GetPrimary();
            if (pri is { } p && p.Spec.Id == id) return p.Connector;
            // id 既不在副库也不在主库 → 回退主库 connector 让下载尝试
            return pri?.Connector;
        }
        return _registry.GetPrimary()?.Connector;
    }

    private void CopyNasPath()
    {
        if (NasPath is { } path)
            Clipboard.SetText(path);
    }

    private void OnSelectionChanged()
    {
        List<GalleryItemViewModel> selection = _gallery.SelectedItems.ToList();
        SelectedCount = selection.Count;
        HasSelection = selection.Count > 0;
        _item = selection.FirstOrDefault();
        _record = _item is not null ? _gallery.FindRecord(_item.FileHash) : null;

        RaiseAllInfoProperties();
    }

    private void RaiseAllInfoProperties()
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(FormatText));
        OnPropertyChanged(nameof(Rating));
        OnPropertyChanged(nameof(CategoryText));
        OnPropertyChanged(nameof(TagsSummary));
        OnPropertyChanged(nameof(SubmittedBy));
        OnPropertyChanged(nameof(ApprovedBy));
        OnPropertyChanged(nameof(ApprovedAtText));
        OnPropertyChanged(nameof(NasPath));
    }
}
