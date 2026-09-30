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
    private bool _isGridView;

    public SharedGalleryViewModel(
        AppSettingsService settings,
        Services.Nas.SharedThumbnailCache thumbnails)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _isGridView = settings.GalleryViewMode == "Grid";
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

    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (SetProperty(ref _isGridView, value))
                OnPropertyChanged(nameof(IsListView));
        }
    }

    public bool IsListView
    {
        get => !_isGridView;
        set => IsGridView = !value;
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

    public int GridCardWidth => GridZoom + 16;
    public int GridCardHeight => GridZoom + 56;
    public int ListRowHeight => ListZoom + 16;

    /// <summary>
    /// Replaces displayed items from the loaded shared records. Id is a local
    /// sequence (shared items never drive local-DB operations); FileHash is
    /// the record id used for thumbnail lookup.
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
                FilePath = string.Empty,
                FileHash = record.Id,
                FileSize = record.FileSize,
                Width = record.Width,
                Height = record.Height,
                Format = record.Format,
                Rating = record.Rating,
                PrimaryCategory = record.PrimaryCategory,
                TagsCsv = string.Join(',', record.Tags),
                SourceBadge = "共享",
                CreatedAt = record.ApprovedAt.DateTime,
            });
            _allItems.Add(item);
            Items.Add(item);
        }
        Refreshed?.Invoke();
        (Thumbnails as Services.Nas.SharedThumbnailCache)?.Prefetch(Items);
    }

    /// <summary>U6-D: full shared record behind a gallery item hash (null when gone).</summary>
    public SharedImageRecord? FindRecord(string hash) =>
        _records.TryGetValue(hash, out var record) ? record : null;

    /// <summary>
    /// U6-D hook: re-applies the in-memory text filter. Empty text shows all.
    /// </summary>
    public void ApplyFilter(string? text)
    {
        Items.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            foreach (var item in _allItems)
                Items.Add(item);
            return;
        }

        string q = text.Trim();
        foreach (var item in _allItems)
        {
            if (item.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                item.TagsCsv.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                Items.Add(item);
            }
        }
    }
}
