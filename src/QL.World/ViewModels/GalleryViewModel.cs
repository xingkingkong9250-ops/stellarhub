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
    private readonly ImageQueryService _query;
    private readonly AppSettingsService _settings;
    private bool _isLoading;
    private bool _isGridView = true;
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

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            LibraryFilter filter = Filter.Clone();
            IReadOnlyList<GalleryListItem> rows =
                await Task.Run(() => _query.QueryGallery(filter));
            int total = await Task.Run(_query.TotalCount);

            Items.Clear();
            foreach (GalleryListItem row in rows)
                Items.Add(new GalleryItemViewModel(row));

            TotalCount = total;
            FilteredCount = rows.Count;
            FilterSummary = BuildSummary(filter);
            Refreshed?.Invoke();
        }
        finally
        {
            IsLoading = false;
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
        return string.Join(" · ", parts);
    }

    private static GallerySort ParseSort(string? value) =>
        Enum.TryParse(value, out GallerySort sort) ? sort : GallerySort.ImportedDesc;
}

public sealed record GallerySortOption(GallerySort Sort, string Display);
