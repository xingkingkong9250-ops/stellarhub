using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.ViewModels.Support;

namespace QL.World.ViewModels;

/// <summary>
/// B2 gallery: server-sorted SQL query surfaced into a UI-virtualized list.
/// </summary>
public sealed class GalleryViewModel : ObservableObject
{
    /// <summary>Rows fetched per page (M4-D server-side pagination).</summary>
    private const int PageSize = 1000;

    private readonly ImageQueryService _query;
    private readonly AppSettingsService _settings;
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _isGridView = true;
    private bool _hasMoreItems;
    private int _totalCount;
    private int _filteredCount;
    private string _filterSummary = string.Empty;

    public GalleryViewModel(
        ImageQueryService query,
        AppSettingsService settings,
        GalleryThumbnailProvider thumbnails)
    {
        _query = query;
        _settings = settings;
        Thumbnails = thumbnails;
        Filter = new LibraryFilter
        {
            Sort = ParseSort(settings.GallerySort),
            ImportedWithinDays = settings.GalleryRecentImportDays,
        };
        _isGridView = settings.GalleryViewMode == "Grid";
    }

    public IReadOnlyList<GallerySortOption> SortOptions { get; } =
    [
        new(GallerySort.ImportedDesc, "导入时间（新→旧）"),
        new(GallerySort.ImportedAsc, "导入时间（旧→新）"),
        new(GallerySort.NameAsc, "文件名 A→Z"),
        new(GallerySort.RatingDesc, "评分（高→低）"),
        new(GallerySort.SizeDesc, "文件大小（大→小）"),
    ];

    public GalleryThumbnailProvider Thumbnails { get; }

    /// <summary>Shared mutable filter driven by B1 and A1.</summary>
    public LibraryFilter Filter { get; }

    public ObservableCollection<GalleryItemViewModel> Items { get; } = [];

    /// <summary>Mirror of ListBox.SelectedItems maintained by the binder behavior.</summary>
    public ObservableCollection<GalleryItemViewModel> SelectedItems { get; } = [];

    /// <summary>Raised after the SelectedItems mirror changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised after a query refresh completes (tree/status reload counts).</summary>
    public event Action? Refreshed;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>True when the current filter yields more rows than are loaded.</summary>
    public bool HasMoreItems
    {
        get => _hasMoreItems;
        private set => SetProperty(ref _hasMoreItems, value);
    }

    /// <summary>Guard for <see cref="LoadMoreAsync"/> re-entrancy.</summary>
    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set => SetProperty(ref _isLoadingMore, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int FilteredCount
    {
        get => _filteredCount;
        private set => SetProperty(ref _filteredCount, value);
    }

    public string FilterSummary
    {
        get => _filterSummary;
        private set => SetProperty(ref _filterSummary, value);
    }

    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (SetProperty(ref _isGridView, value))
            {
                OnPropertyChanged(nameof(IsListView));
                _settings.GalleryViewMode = value ? "Grid" : "List";
            }
        }
    }

    public bool IsListView
    {
        get => !_isGridView;
        set => IsGridView = !value;
    }

    public GallerySort Sort
    {
        get => Filter.Sort;
        set
        {
            if (Filter.Sort != value)
            {
                Filter.Sort = value;
                OnPropertyChanged();
                _settings.GallerySort = value.ToString();
                _ = RefreshAsync();
            }
        }
    }

    public int GridZoom
    {
        get => _settings.GalleryGridZoom;
        set
        {
            if (_settings.GalleryGridZoom != value)
            {
                _settings.GalleryGridZoom = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GridCardWidth));
                OnPropertyChanged(nameof(GridCardHeight));
            }
        }
    }

    public int ListZoom
    {
        get => _settings.GalleryListZoom;
        set
        {
            if (_settings.GalleryListZoom != value)
            {
                _settings.GalleryListZoom = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ListRowHeight));
            }
        }
    }

    /// <summary>VirtualizingTilePanel cell width (image edge + card padding).</summary>
    public int GridCardWidth => GridZoom + 16;

    /// <summary>Cell height (image edge + caption block).</summary>
    public int GridCardHeight => GridZoom + 56;

    /// <summary>List-mode row height driven by B2-4.</summary>
    public int ListRowHeight => ListZoom + 16;

    public GalleryItemViewModel? PrimarySelected =>
        SelectedItems.Count > 0 ? SelectedItems[0] : null;

    public void NotifySelectionChanged() => SelectionChanged?.Invoke();

    /// <summary>
    /// F-09/F-10: generation counter. Each call to RefreshAsync bumps the
    /// generation; the body captures it. If a second RefreshAsync starts
    /// before the first finishes (rapid category/search switches), the first
    /// sees generation mismatch and skips overwriting Items — the last writer
    /// wins, preventing stale results from replacing fresh ones.
    /// </summary>
    private int _refreshGeneration;

    public async Task RefreshAsync()
    {
        int generation = Interlocked.Increment(ref _refreshGeneration);
        IsLoading = true;
        _isLoadingMore = false;
        try
        {
            LibraryFilter filter = Filter.Clone();
            filter.Limit = PageSize;
            filter.Offset = 0;
            IReadOnlyList<GalleryListItem> rows =
                await Task.Run(() => _query.QueryGallery(filter));
            int filtered = await Task.Run(() => _query.CountFiltered(filter));
            int total = await Task.Run(_query.TotalCount);

            // F-09/F-10: discard stale results — a newer RefreshAsync has
            // already started and will deliver its own rows.
            if (generation != _refreshGeneration)
                return;

            Items.Clear();
            foreach (GalleryListItem row in rows)
                Items.Add(new GalleryItemViewModel(row));

            TotalCount = total;
            FilteredCount = filtered;
            HasMoreItems = Items.Count < filtered;
            FilterSummary = BuildSummary(filter);
            Refreshed?.Invoke();
            // M5-H: prefetch thumbnails for the freshly loaded page so the
            // next scroll batch is already on disk. Service skips items that
            // already exist; remote items are skipped on weak networks.
            Thumbnails.Prefetch(Items);
        }
        finally
        {
            // Only the latest generation controls the busy indicator.
            if (generation == _refreshGeneration)
                IsLoading = false;
        }
    }

    /// <summary>
    /// Appends the next page when the gallery is scrolled near the bottom.
    /// Safe to call repeatedly; no-op while a load is already running.
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (_isLoading || _isLoadingMore || !HasMoreItems)
            return;

        IsLoadingMore = true;
        try
        {
            LibraryFilter filter = Filter.Clone();
            filter.Limit = PageSize;
            filter.Offset = Items.Count;
            IReadOnlyList<GalleryListItem> rows =
                await Task.Run(() => _query.QueryGallery(filter));
            int filtered = await Task.Run(() => _query.CountFiltered(filter));

            foreach (GalleryListItem row in rows)
                Items.Add(new GalleryItemViewModel(row));
            // F-13: compare against the filtered total, not the page size —
            // a short last page must not be mistaken for "more available".
            HasMoreItems = Items.Count < filtered;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    /// <summary>Re-reads mutable fields (rating/category/tags) without rebuilding filter.</summary>
    public async Task RefreshCurrentAsync()
    {
        HashSet<long> preserve = SelectedItems.Select(i => i.Id).ToHashSet();

        await RefreshAsync();

        // Selection survives a refresh where rows still match the filter.
        SelectedItems.Clear();
        foreach (GalleryItemViewModel item in Items)
            if (preserve.Contains(item.Id))
                SelectedItems.Add(item);
        NotifySelectionChanged();
    }

    private static string BuildSummary(LibraryFilter filter)
    {
        var parts = new List<string>();
        if (filter.Uncategorized)
            parts.Add("未分类");
        else if (!string.IsNullOrEmpty(filter.Category))
            parts.Add(filter.Category);
        if (!string.IsNullOrEmpty(filter.Tag))
            parts.Add($"# {filter.Tag}");
        if (!string.IsNullOrWhiteSpace(filter.Text))
            parts.Add($"“{filter.Text.Trim()}”");
        // U2-B: reflect 最近入库 in the filter summary chip.
        if (filter.ImportedWithinDays is int days && days > 0)
            parts.Add(days == 1 ? "今天" : $"近 {days} 天");
        // U3-A: attribute filter dimensions.
        if (!string.IsNullOrEmpty(filter.ColorFamily))
            parts.Add(AttributeFilterDisplay.Color(filter.ColorFamily));
        if (!string.IsNullOrEmpty(filter.ResolutionBucket))
            parts.Add(AttributeFilterDisplay.Resolution(filter.ResolutionBucket));
        if (!string.IsNullOrEmpty(filter.FileSizeBucket))
            parts.Add(AttributeFilterDisplay.FileSize(filter.FileSizeBucket));
        if (!string.IsNullOrEmpty(filter.RatingBucket))
            parts.Add(AttributeFilterDisplay.Rating(filter.RatingBucket));
        return string.Join(" · ", parts);
    }

    private static GallerySort ParseSort(string? value) =>
        Enum.TryParse(value, out GallerySort sort) ? sort : GallerySort.ImportedDesc;
}

public sealed record GallerySortOption(GallerySort Sort, string Display);
