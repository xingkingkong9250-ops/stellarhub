using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// B3 detail panel: single-image mode (preview/info/rating/tags/category/notes)
/// and multi-select batch mode (bulk tag/category/rating edits).
/// </summary>
public sealed class DetailViewModel : ObservableObject
{
    private const int NOTES_DEBOUNCE_MS = 800;

    private readonly ImageQueryService _query;
    private readonly TagService _tags;
    private readonly GalleryViewModel _gallery;
    private readonly SemanticSearchService _semanticSearch;
    private readonly DispatcherTimer _notesTimer;
    private readonly DispatcherTimer _saveHintTimer;

    private long _editingImageId;
    private string _notesText = string.Empty;
    private int _rating;
    private CategoryOption? _selectedCategory;
    private bool _hasSelection;
    private bool _isBatch;
    private int _selectedCount;
    private string _saveHint = string.Empty;
    private string _newTagText = string.Empty;
    private string _batchAddTagsText = string.Empty;
    private string _batchRemoveTagsText = string.Empty;

    public DetailViewModel(
        ImageQueryService query,
        TagService tags,
        GalleryViewModel gallery,
        SemanticSearchService semanticSearch)
    {
        _query = query;
        _tags = tags;
        _gallery = gallery;
        _semanticSearch = semanticSearch;

        CategoryOptions = new List<CategoryOption>
        {
            new("未分类", null),
        };
        foreach (string category in Categories.All)
            CategoryOptions.Add(new CategoryOption(category, category));
        _selectedCategory = CategoryOptions[0];

        _notesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(NOTES_DEBOUNCE_MS) };
        _notesTimer.Tick += (_, _) =>
        {
            _notesTimer.Stop();
            PersistNotes();
        };
        _saveHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveHintTimer.Tick += (_, _) =>
        {
            _saveHintTimer.Stop();
            SaveHint = string.Empty;
        };

        SetRatingCommand = new RelayCommand(SetRating);
        AddTagCommand = new RelayCommand(AddTag, () => HasSelection && !IsBatch);
        RemoveTagCommand = new RelayCommand(RemoveTag);
        OpenOriginalCommand = new RelayCommand(OpenOriginal, () => HasSelection && !IsBatch);
        RevealLocationCommand = new RelayCommand(RevealLocation, () => HasSelection && !IsBatch);
        CopyPathCommand = new RelayCommand(CopyPath, () => HasSelection && !IsBatch);
        BatchAddTagsCommand = new RelayCommand(BatchAddTags, () => IsBatch);
        BatchRemoveTagsCommand = new RelayCommand(BatchRemoveTags, () => IsBatch);
        BatchRatingCommand = new RelayCommand(BatchSetRating, _ => IsBatch);
        BatchCategoryCommand = new RelayCommand(BatchSetCategory, _ => IsBatch);
        RelocateFileCommand = new RelayCommand(RelocateFile, () => IsMissing && IsSingle);
        SelectRelatedCommand = new RelayCommand(SelectRelated);

        gallery.SelectionChanged += OnSelectionChanged;
    }

    // ─── State ──────────────────────────────────────────────────────────────

    public List<CategoryOption> CategoryOptions { get; }

    public ObservableCollection<DetailTagViewModel> ImageTags { get; } = [];

    /// <summary>U2-C (§4.B.3): B3-9 相关资产缩略图条（top 6），复用 SemanticSearchService 缓存。</summary>
    public ObservableCollection<GalleryItemViewModel> RelatedAssets { get; } = [];

    public bool HasRelatedAssets => RelatedAssets.Count > 0;

    /// <summary>U2-C: raised when the user clicks a related-asset thumbnail; ShellViewModel runs FindSimilar.</summary>
    public event Action<long>? FindSimilarRequested;

    public bool HasSelection
    {
        get => _hasSelection;
        private set
        {
            if (SetProperty(ref _hasSelection, value))
            {
                // F-07: must also notify IsSingle — on the FIRST single selection
                // IsBatch never changes (stays false), so its setter doesn't fire
                // and IsSingle would stay stuck at its initial false value.
                OnPropertyChanged(nameof(IsSingle));
                OnPropertyChanged(nameof(ShowEmpty));
                CommandManagerInvalidate();
            }
        }
    }

    public bool IsBatch
    {
        get => _isBatch;
        private set
        {
            if (SetProperty(ref _isBatch, value))
            {
                OnPropertyChanged(nameof(IsSingle));
                OnPropertyChanged(nameof(ShowEmpty));
                CommandManagerInvalidate();
            }
        }
    }

    public bool IsSingle => HasSelection && !IsBatch;
    public bool ShowEmpty => !HasSelection;

    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetProperty(ref _selectedCount, value);
    }

    private GalleryItemViewModel? Primary { get; set; }

    private ImageRecord? Record { get; set; }

    public string? FileName => Record?.FileName;
    public string? FilePath => Record?.FilePath;
    public string? FormatText => Record?.Format?.ToUpperInvariant();
    public string? DimensionsText => Primary?.DimensionsText;
    public string? FileSizeText => Primary?.FileSizeText;
    public string? CreatedAtText => Record?.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public bool IsMissing => Record?.IsMissing ?? false;

    public GalleryItemViewModel? Preview => Primary;

    /// <summary>U4-A (B3-4 主色调)：解析 images.dominant_colors 得到的 "#RRGGBB" 列表；未分析/数据损坏时为空。</summary>
    public IReadOnlyList<string> DominantColors { get; private set; } = [];

    public bool HasDominantColors => DominantColors.Count > 0;

    public int Rating
    {
        get => _rating;
        set => SetProperty(ref _rating, value);
    }

    public CategoryOption? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value) && IsSingle && Record is not null)
            {
                string? newCategory = value?.Value;
                if (Record.PrimaryCategory != newCategory)
                {
                    _query.UpdateCategory(Record.Id, newCategory);
                    Record.PrimaryCategory = newCategory;
                    _ = _gallery.RefreshCurrentAsync();
                }
            }
        }
    }

    public string NotesText
    {
        get => _notesText;
        set
        {
            if (SetProperty(ref _notesText, value))
                _notesTimer.StopAndStart();
        }
    }

    public string SaveHint
    {
        get => _saveHint;
        private set => SetProperty(ref _saveHint, value);
    }

    public string NewTagText
    {
        get => _newTagText;
        set => SetProperty(ref _newTagText, value);
    }

    public string BatchAddTagsText
    {
        get => _batchAddTagsText;
        set => SetProperty(ref _batchAddTagsText, value);
    }

    public string BatchRemoveTagsText
    {
        get => _batchRemoveTagsText;
        set => SetProperty(ref _batchRemoveTagsText, value);
    }

    // ─── Commands ─────────────────────────────────────────────────────────

    public RelayCommand SetRatingCommand { get; }
    public RelayCommand AddTagCommand { get; }
    public RelayCommand RemoveTagCommand { get; }
    public RelayCommand OpenOriginalCommand { get; }
    public RelayCommand RevealLocationCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand BatchAddTagsCommand { get; }
    public RelayCommand BatchRemoveTagsCommand { get; }
    public RelayCommand BatchRatingCommand { get; }
    public RelayCommand BatchCategoryCommand { get; }
    public RelayCommand RelocateFileCommand { get; }
    public RelayCommand SelectRelatedCommand { get; }

    private void SetRating(object? parameter)
    {
        if (!IsSingle || Record is null || parameter is null) return;
        int chosen = System.Convert.ToInt32(parameter);
        int next = Record.Rating == chosen ? 0 : chosen;
        _query.UpdateRating(Record.Id, next);
        Record.Rating = next;
        Rating = next;
        _ = _gallery.RefreshCurrentAsync();
    }

    private void AddTag()
    {
        if (!IsSingle || Record is null || string.IsNullOrWhiteSpace(NewTagText)) return;

        _tags.AddTags([Record.Id], [NewTagText], TagSource.Manual);
        NewTagText = string.Empty;
        _ = ReloadSingleAsync(Record.Id);
        _ = _gallery.RefreshCurrentAsync();
    }

    private void RemoveTag(object? parameter)
    {
        if (Record is null || parameter is not DetailTagViewModel tag) return;
        _tags.RemoveTag(Record.Id, tag.Tag);
        _ = ReloadSingleAsync(Record.Id);
        _ = _gallery.RefreshCurrentAsync();
    }

    private void BatchAddTags()
    {
        if (!IsBatch || string.IsNullOrWhiteSpace(BatchAddTagsText)) return;
        List<long> ids = SelectedIds();
        _tags.AddTags(ids, [BatchAddTagsText], TagSource.Manual);
        BatchAddTagsText = string.Empty;
        _ = _gallery.RefreshCurrentAsync();
    }

    private void BatchRemoveTags()
    {
        if (!IsBatch || string.IsNullOrWhiteSpace(BatchRemoveTagsText)) return;
        List<long> ids = SelectedIds();
        foreach (string raw in BatchRemoveTagsText.Split(
                     [',', '，', '、', ';', '；', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (long id in ids)
                _tags.RemoveTag(id, raw);
        BatchRemoveTagsText = string.Empty;
        _ = _gallery.RefreshCurrentAsync();
    }

    private void BatchSetRating(object? parameter)
    {
        if (!IsBatch || parameter is null) return;
        _query.SetRatingBatch(SelectedIds(), System.Convert.ToInt32(parameter));
        _ = _gallery.RefreshCurrentAsync();
    }

    private void BatchSetCategory(object? parameter)
    {
        if (!IsBatch) return;
        string? category = parameter as string;
        _query.SetCategoryBatch(SelectedIds(), category);
        _ = _gallery.RefreshCurrentAsync();
    }

    private void OpenOriginal()
    {
        if (Record is not null)
            SystemNavigator.OpenFile(Record.FilePath);
    }

    private void RevealLocation()
    {
        if (Record is not null)
            SystemNavigator.RevealFile(Record.FilePath);
    }

    private void CopyPath()
    {
        if (Record is not null)
            Clipboard.SetText(Record.FilePath);
    }

    /// <summary>B3-2 重新定位：选择新路径，更新记录并刷新画廊。</summary>
    private void RelocateFile()
    {
        if (!IsSingle || Record is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择替代文件",
            Filter = "图片文件 (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.tga)|"
                     + "*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.tga|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        string newPath = dialog.FileName;
        bool ok = _query.RelocateFile(Record.Id, newPath);
        if (!ok)
        {
            MessageBox.Show(
                "该文件已在库中存在（内容重复），无法重新定位。",
                "重新定位失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _ = ReloadSingleAsync(Record.Id);
        _ = _gallery.RefreshCurrentAsync();
    }

    // ─── Selection wiring ──────────────────────────────────────────────────

    private void OnSelectionChanged()
    {
        _notesTimer.Stop();
        // M5-F: clear the previous detail item's preview request so a stale
        // ThumbnailReady callback doesn't overwrite the new item's PreviewImage.
        if (Primary is not null)
        {
            Primary.PreviewImage = null;
            _gallery.Thumbnails.ClearPreviewRequest(Primary.FileHash);
        }

        List<GalleryItemViewModel> selection = _gallery.SelectedItems.ToList();
        SelectedCount = selection.Count;
        HasSelection = selection.Count > 0;
        IsBatch = selection.Count > 1;
        Primary = selection.FirstOrDefault();

        if (IsSingle && Primary is not null)
        {
            // M5-F: request the 1024px preview tier for the detail pane.
            // The ProgressiveImage control cross-fades thumb → preview when
            // the PreviewImage property is set by GalleryThumbnailProvider.
            _gallery.Thumbnails.RequestPreview(Primary);
            _ = ReloadSingleAsync(Primary.Id);
        }
        else
        {
            Record = null;
            DominantColors = [];
        }

        RaiseAllInfoProperties();
    }

    private async Task ReloadSingleAsync(long id)
    {
        ImageRecord? record = await Task.Run(() => _query.GetById(id));
        if (record is null) return;
        Record = record;
        DominantColors = ParseDominantColors(record.DominantColorsJson);

        Rating = record.Rating;
        _notesText = record.Notes;
        OnPropertyChanged(nameof(NotesText));
        _editingImageId = record.Id;
        SelectedCategory = CategoryOptions.FirstOrDefault(
            o => o.Value == record.PrimaryCategory) ?? CategoryOptions[0];

        IReadOnlyList<ImageTagRecord> tags = await Task.Run(() => _tags.GetTagsForImage(id));
        ImageTags.Clear();
        foreach (ImageTagRecord tag in tags)
            ImageTags.Add(new DetailTagViewModel(
                tag.Tag, tag.Source == TagSource.Ai, RemoveTagCommand));

        RaiseAllInfoProperties();
        // U2-C: load related assets after the main record is ready.
        _ = LoadRelatedAsync(id);
    }

    /// <summary>
    /// U2-C (§4.B.3): fetch top-6 semantically similar images and surface them
    /// in the B3-9 thumbnail strip. Reuses SemanticSearchService's in-memory
    /// embedding cache; no-op (section hidden) when the source has no embedding.
    /// </summary>
    private async Task LoadRelatedAsync(long sourceId)
    {
        RelatedAssets.Clear();
        OnPropertyChanged(nameof(HasRelatedAssets));

        IReadOnlyList<long>? ids = await Task.Run(() => _semanticSearch.SearchByImageId(sourceId));
        if (ids is null || ids.Count == 0) return;

        foreach (long id in ids.Take(6))
        {
            ImageRecord? rec = await Task.Run(() => _query.GetById(id));
            if (rec is null) continue;

            var item = new GalleryItemViewModel(new GalleryListItem
            {
                Id = rec.Id,
                FileName = rec.FileName,
                FilePath = rec.FilePath,
                FileHash = rec.FileHash,
                FileSize = rec.FileSize,
                Width = rec.Width,
                Height = rec.Height,
                Format = rec.Format,
                Rating = rec.Rating,
                PrimaryCategory = rec.PrimaryCategory,
                IsMissing = rec.IsMissing,
                CreatedAt = rec.CreatedAt,
            });
            RelatedAssets.Add(item);
            // Reuse the gallery thumbnail pipeline (cache + webp decode).
            _gallery.Thumbnails.Attach(item);
        }

        OnPropertyChanged(nameof(HasRelatedAssets));
    }

    /// <summary>U2-C: clicking a related-asset thumbnail dives into its similarity neighborhood.</summary>
    private void SelectRelated(object? parameter)
    {
        if (parameter is GalleryItemViewModel item)
            FindSimilarRequested?.Invoke(item.Id);
    }

    private void PersistNotes()
    {
        if (Record is null || _editingImageId != Record.Id) return;
        _query.UpdateNotes(Record.Id, NotesText);
        Record.Notes = NotesText;
        SaveHint = "已保存";
        _saveHintTimer.Stop();
        _saveHintTimer.Start();
    }

    private List<long> SelectedIds() =>
        _gallery.SelectedItems.Select(i => i.Id).ToList();

    private void RaiseAllInfoProperties()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FormatText));
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(CreatedAtText));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(DominantColors));
        OnPropertyChanged(nameof(HasDominantColors));
    }

    /// <summary>dominant_colors 是 JSON 数组；null/空/损坏 JSON 一律解析为空列表（B3-4 整行隐藏）。</summary>
    private static IReadOnlyList<string> ParseDominantColors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void CommandManagerInvalidate() =>
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
}

public sealed record CategoryOption(string Display, string? Value);

/// <summary>Tag chip inside B3-5.</summary>
public sealed class DetailTagViewModel
{
    public DetailTagViewModel(string tag, bool isAi, RelayCommand removeCommand)
    {
        Tag = tag;
        IsAi = isAi;
        RemoveCommand = removeCommand;
    }

    public string Tag { get; }
    public bool IsAi { get; }
    public string SourceText => IsAi ? "AI 标签" : "手动标签";
    public RelayCommand RemoveCommand { get; }
}

internal static class DispatcherTimerExtensions
{
    public static void StopAndStart(this DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }
}
