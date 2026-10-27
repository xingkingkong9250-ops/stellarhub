using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// U6-A: gallery view model backed by the NAS shared library
/// (<c>library.json</c>). Mirrors the surface of <see cref="GalleryViewModel"/>
/// (items, selection, grid/list, zoom metrics) so the existing card/row
/// templates and virtualizing panels render shared images unchanged.
/// Thumbnails come from <see cref="Services.Nas.SharedThumbnailCache"/>.
/// </summary>
public sealed class SharedGalleryViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly List<GalleryItemViewModel> _allItems = [];
    private readonly Dictionary<string, SharedImageRecord> _records = new(StringComparer.Ordinal);
    private string _viewMode = "Grid";
    private string? _textFilter;
    private HashSet<string>? _collectionIds;

    public SharedGalleryViewModel(
        AppSettingsService settings,
        Services.Nas.SharedThumbnailCache thumbnails)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _viewMode = settings.GalleryViewMode switch
        {
            "List" => "List",
            "Browse" => "Browse",
            _ => "Grid",
        };
    }

    public IGalleryThumbnailProvider Thumbnails { get; }

    public ObservableCollection<GalleryItemViewModel> Items { get; } = [];

    /// <summary>Mirror of ListBox.SelectedItems maintained by the binder behavior.</summary>
    public ObservableCollection<GalleryItemViewModel> SelectedItems { get; } = [];

    public event Action? SelectionChanged;
    public event Action? Refreshed;

    public GalleryItemViewModel? PrimarySelected =>
        SelectedItems.Count > 0 ? SelectedItems[0] : null;

    public void NotifySelectionChanged() => SelectionChanged?.Invoke();

    /// <summary>
    /// U6-A: shared gallery view mode mirrors the local Gallery; pass-through
    /// booleans for XAML Toggle buttons. Setter does NOT persist (the local
    /// <see cref="GalleryViewModel"/> owns persistence; shared follows UI state).
    /// </summary>
    public string ViewMode
    {
        get => _viewMode;
        set
        {
            string coerced = value switch
            {
                "List" => "List",
                "Browse" => "Browse",
                _ => "Grid",
            };
            if (SetProperty(ref _viewMode, coerced))
            {
                OnPropertyChanged(nameof(IsGridView));
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(IsBrowseView));
            }
        }
    }

    public bool IsGridView
    {
        get => _viewMode == "Grid";
        set { if (value) ViewMode = "Grid"; }
    }

    public bool IsListView
    {
        get => _viewMode == "List";
        set { if (value) ViewMode = "List"; }
    }

    public bool IsBrowseView
    {
        get => _viewMode == "Browse";
        set { if (value) ViewMode = "Browse"; }
    }

    // Zoom metrics delegate to the same settings as the local gallery so the
    // DataTemplates' relative bindings (DataContext.GridZoom etc.) resolve.

    public int GridZoom
    {
        get => _settings.GalleryGridZoom;
        set => _settings.GalleryGridZoom = value;
    }

    public int ListZoom
    {
        get => _settings.GalleryListZoom;
        set => _settings.GalleryListZoom = value;
    }

    public int BrowseZoom
    {
        get => _settings.GalleryBrowseZoom;
        set => _settings.GalleryBrowseZoom = value;
    }

    public int GridCardWidth => GridZoom + 16;
    public int GridCardHeight => GridZoom + 56;
    public int ListRowHeight => ListZoom + 16;
    public int BrowseCardWidth => BrowseZoom;

    /// <summary>
    /// Replaces displayed items from the loaded shared records. Id is a local
    /// sequence (shared items never drive local-DB operations); FileHash is
    /// the record id used for thumbnail lookup.
    /// M8-C: 聚合后每条 record 都带 SourceNasId（AggregatedSharedLibraryService
    /// 已注入）；GalleryListItem.NasConnectionId + Source=Remote 让
    /// GalleryThumbnailProvider.CreateSource 自动按 NasConnectionId 路由到对应
    /// 副库 connector。FilePath 编码为 <c>nas://{id}{SourceNasPath}</c> 以兼容
    /// StripNasPrefix 还原逻辑（line 250-301）。
    /// </summary>
    public void Load(IReadOnlyList<SharedImageRecord> records)
    {
        _allItems.Clear();
        _records.Clear();
        Items.Clear();
        int sequence = 0;
        foreach (var record in records)
        {
            sequence++;
            _records[record.Id] = record;
            var item = new GalleryItemViewModel(new GalleryListItem
            {
                Id = sequence,
                FileName = record.FileName,
                FilePath = BuildCanonicalPath(record),
                FileHash = record.Id,
                FileSize = record.FileSize,
                Width = record.Width,
                Height = record.Height,
                Format = record.Format,
                Rating = record.Rating,
                PrimaryCategory = record.PrimaryCategory,
                TagsCsv = string.Join(',', record.Tags),
                Source = ImageSource.Remote,
                NasConnectionId = record.SourceNasId,
                // M8-E: ShowSourceBadge=true + 有 SourceNasId → 显示 nas://{id} 来源徽章；
                // 否则回退"共享"静态值（兼容 M8 前行为，徽章不隐藏）。
                SourceBadge = (record.SourceNasId is int nid && _settings.ShowSourceBadge)
                    ? $"nas://{nid}"
                    : "共享",
                CreatedAt = record.ApprovedAt.DateTime,
            });
            _allItems.Add(item);
        }
        Rebuild();
        Refreshed?.Invoke();
        (Thumbnails as Services.Nas.SharedThumbnailCache)?.Prefetch(Items);
    }

    /// <summary>
    /// PEND-004: true when a collection filter is narrowing the list. Drives the
    /// empty-state wording in the shell.
    /// </summary>
    public bool HasCollectionFilter => _collectionIds is not null;

    /// <summary>
    /// M8-C: 构造 <c>nas://{SourceNasId}{SourceNasPath}</c> 规范路径，供
    /// GalleryThumbnailProvider.StripNasPrefix 还原 remote path。当 SourceNasId
    /// 或 SourceNasPath 缺失时返回空串（缩略图加载会失败回占位符，同本地缺失文件 UX）。
    /// </summary>
    private static string BuildCanonicalPath(SharedImageRecord r)
    {
        if (r.SourceNasId is int id && r.SourceNasPath is { } path)
            return $"nas://{id}{path}";
        return string.Empty;
    }

    /// <summary>U6-D: full shared record behind a gallery item hash (null when gone).</summary>
    public SharedImageRecord? FindRecord(string hash) =>
        _records.TryGetValue(hash, out var record) ? record : null;

    /// <summary>
    /// U6-D hook: re-applies the in-memory text filter. Empty text shows all.
    /// </summary>
    public void ApplyFilter(string? text)
    {
        _textFilter = text;
        Rebuild();
    }

    /// <summary>
    /// PEND-004: narrows the gallery to a shared collection's image hashes
    /// (null = all images). Combines with the active text filter.
    /// </summary>
    public void ApplyCollectionFilter(IReadOnlyCollection<string>? imageIds)
    {
        _collectionIds = imageIds is null
            ? null
            : new HashSet<string>(imageIds, StringComparer.Ordinal);
        OnPropertyChanged(nameof(HasCollectionFilter));
        Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        string? q = string.IsNullOrWhiteSpace(_textFilter) ? null : _textFilter.Trim();
        foreach (var item in _allItems)
        {
            if (_collectionIds is not null && !_collectionIds.Contains(item.FileHash)) continue;
            if (q is not null &&
                !item.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.TagsCsv.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Items.Add(item);
        }
    }
}
