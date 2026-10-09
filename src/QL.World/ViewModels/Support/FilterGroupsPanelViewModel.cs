using System.Collections.ObjectModel;
using System.Windows.Media;
using QL.World.Helpers;
using QL.World.Models.Taxonomy;
using QL.World.Services;

namespace QL.World.ViewModels.Support;

/// <summary>
/// M12-F: B1-6 筛选标签组 — 5 组标签多选 chips（美术风格/画面构图/灯光氛围/
/// 题材气质/状态形容）+ 文件格式多选 + 色彩倾向色板。组内 OR、组间 AND
/// （语义由 LibraryFilter 各字段 + ImageQueryService 定义）。组内已选集合
/// 非空时写入对应 LibraryFilter 字段，全不选时写 null（该维度不激活）。
/// 色板 hex 经 ColorFamily.HexToFamily 映射为色族后匹配 i.color_family。
/// </summary>
public sealed class FilterGroupsPanelViewModel : ObservableObject
{
    private readonly GalleryViewModel _gallery;
    private readonly AppSettingsService _settings;

    public FilterGroupsPanelViewModel(GalleryViewModel gallery, AppSettingsService settings)
    {
        _gallery = gallery;
        _settings = settings;

        GroupsBeforePalette =
        [
            new FilterGroupViewModel(
                TagTaxonomy.GroupArtStyle,
                TagTaxonomy.TagsByGroup[TagTaxonomy.GroupArtStyle],
                list => ApplyAndRefresh(() => _gallery.Filter.ArtStyleTags = list)),
            new FilterGroupViewModel(
                TagTaxonomy.GroupFileFormat,
                TagTaxonomy.TagsByGroup[TagTaxonomy.GroupFileFormat],
                list => ApplyAndRefresh(() => _gallery.Filter.FileFormats = list)),
        ];
        GroupsAfterPalette =
        [
            new FilterGroupViewModel(
                TagTaxonomy.GroupComposition,
                TagTaxonomy.TagsByGroup[TagTaxonomy.GroupComposition],
                list => ApplyAndRefresh(() => _gallery.Filter.CompositionTags = list)),
            new FilterGroupViewModel(
                TagTaxonomy.GroupLighting,
                TagTaxonomy.TagsByGroup[TagTaxonomy.GroupLighting],
                list => ApplyAndRefresh(() => _gallery.Filter.LightingTags = list)),
            new FilterGroupViewModel(
                TagTaxonomy.GroupTheme,
                TagTaxonomy.TagsByGroup[TagTaxonomy.GroupTheme],
                list => ApplyAndRefresh(() => _gallery.Filter.ThemeTags = list)),
            new FilterGroupViewModel(
                TagTaxonomy.GroupCondition,
                TagTaxonomy.TagsByGroup[TagTaxonomy.GroupCondition],
                list => ApplyAndRefresh(() => _gallery.Filter.ConditionTags = list)),
        ];
        Palette = new ColorPaletteViewModel(
            PaletteHexes,
            list => ApplyAndRefresh(() => _gallery.Filter.ColorPaletteHexes = list));
    }

    /// <summary>B1-6 section expanded state; persisted, default collapsed.</summary>
    public bool IsExpanded
    {
        get => _settings.SidebarFilterGroupsExpanded;
        set
        {
            if (_settings.SidebarFilterGroupsExpanded != value)
            {
                _settings.SidebarFilterGroupsExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>色板之前的 chip 组（美术风格、文件格式）。</summary>
    public IReadOnlyList<FilterGroupViewModel> GroupsBeforePalette { get; }

    /// <summary>B1-6-3 色彩倾向色板。</summary>
    public ColorPaletteViewModel Palette { get; }

    /// <summary>色板之后的 chip 组（画面构图、灯光氛围、题材气质、状态形容）。</summary>
    public IReadOnlyList<FilterGroupViewModel> GroupsAfterPalette { get; }

    /// <summary>清除全部筛选时重置所有 chips 与色板（不触发画廊刷新，由调用方统一刷新）。</summary>
    public void ResetSelection()
    {
        foreach (FilterGroupViewModel group in GroupsBeforePalette)
            group.ResetSelection();
        foreach (FilterGroupViewModel group in GroupsAfterPalette)
            group.ResetSelection();
        Palette.ResetSelection();
    }

    private void ApplyAndRefresh(Action setField)
    {
        setField();
        _ = _gallery.RefreshAsync();
    }

    /// <summary>
    /// B1-6-3 色板色值（多选，各 hex 独立映射色族后 OR）。8 个饱和色 +
    /// 7 个中间调 + 4 个中性色；中性色经 HexToFamily 均映射为灰族。
    /// </summary>
    public static IReadOnlyList<string> PaletteHexes { get; } =
    [
        "#FF3B30", "#FF9500", "#FFCC00", "#34C759",
        "#32ADE6", "#007AFF", "#AF52DE", "#FF2D55",
        "#8B4513", "#F4A460", "#FFD700", "#90EE90",
        "#87CEEB", "#FFB6C1", "#DDA0DD",
        "#FFFFFF", "#8E8E93", "#4A4A4A", "#000000",
    ];
}

/// <summary>单个筛选标签组：组名 + 多选 chips + 写回 LibraryFilter 的回调。</summary>
public sealed class FilterGroupViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<string>?> _apply;

    public FilterGroupViewModel(
        string groupName,
        IReadOnlyList<string> tags,
        Action<IReadOnlyList<string>?> apply)
    {
        GroupName = groupName;
        _apply = apply;
        foreach (string tag in tags)
            Chips.Add(new FilterTagChipViewModel(tag, Toggle));
    }

    public string GroupName { get; }

    public ObservableCollection<FilterTagChipViewModel> Chips { get; } = [];

    /// <summary>组内是否有选中项（未用：预留折叠头计数展示）。</summary>
    public bool HasSelection => Chips.Any(c => c.IsActive);

    /// <summary>清除全部筛选：全部取消选中并写 null（不刷新画廊）。</summary>
    public void ResetSelection()
    {
        foreach (FilterTagChipViewModel chip in Chips)
            chip.IsActive = false;
        _apply(null);
    }

    private void Toggle(FilterTagChipViewModel chip)
    {
        chip.IsActive = !chip.IsActive;
        List<string> selected = Chips.Where(c => c.IsActive).Select(c => c.Tag).ToList();
        _apply(selected.Count > 0 ? selected : null);
    }
}

/// <summary>筛选标签组中的一个 chip。</summary>
public sealed class FilterTagChipViewModel : ObservableObject
{
    private bool _isActive;

    public FilterTagChipViewModel(string tag, Action<FilterTagChipViewModel> toggle)
    {
        Tag = tag;
        SelectCommand = new RelayCommand(() => toggle(this));
    }

    public string Tag { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>B1-6-3 色彩倾向色板：固定色值多选，写 ColorPaletteHexes。</summary>
public sealed class ColorPaletteViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<string>?> _apply;

    public ColorPaletteViewModel(IReadOnlyList<string> hexes, Action<IReadOnlyList<string>?> apply)
    {
        _apply = apply;
        foreach (string hex in hexes)
            Swatches.Add(new ColorSwatchViewModel(hex, Toggle));
    }

    public ObservableCollection<ColorSwatchViewModel> Swatches { get; } = [];

    /// <summary>清除全部筛选：全部取消选中并写 null（不刷新画廊）。</summary>
    public void ResetSelection()
    {
        foreach (ColorSwatchViewModel swatch in Swatches)
            swatch.IsActive = false;
        _apply(null);
    }

    private void Toggle(ColorSwatchViewModel swatch)
    {
        swatch.IsActive = !swatch.IsActive;
        List<string> selected = Swatches.Where(s => s.IsActive).Select(s => s.Hex).ToList();
        _apply(selected.Count > 0 ? selected : null);
    }
}

/// <summary>色板中的一个色块。</summary>
public sealed class ColorSwatchViewModel : ObservableObject
{
    private bool _isActive;

    public ColorSwatchViewModel(string hex, Action<ColorSwatchViewModel> toggle)
    {
        Hex = hex;
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Brush = brush;
        SelectCommand = new RelayCommand(() => toggle(this));
    }

    /// <summary>"#RRGGBB"（写入 LibraryFilter.ColorPaletteHexes）。</summary>
    public string Hex { get; }

    /// <summary> Frozen swatch fill brush.</summary>
    public SolidColorBrush Brush { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
