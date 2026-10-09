using QL.World.Models.Taxonomy;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>
/// M12-F: B1-6 筛选标签组 VM — chip 多选写回 LibraryFilter 各字段、
/// 色板多选写 ColorPaletteHexes、ResetSelection 全清。
/// </summary>
public sealed class FilterGroupsPanelViewModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly AppSettingsService _settings;
    private readonly GalleryViewModel _gallery;
    private readonly FilterGroupsPanelViewModel _panel;

    public FilterGroupsPanelViewModelTests()
    {
        _settings = new AppSettingsService(_lib.DirectoryRoot);
        _settings.Load();
        _gallery = new GalleryViewModel(
            new ImageQueryService(_lib.Db),
            _settings,
            new GalleryThumbnailProvider(new ThumbnailService(_lib.DirectoryRoot, _settings)));
        _panel = new FilterGroupsPanelViewModel(_gallery, _settings);
    }

    [Fact]
    public void Panel_builds_six_chip_groups_and_palette()
    {
        Assert.Equal(2, _panel.GroupsBeforePalette.Count);
        Assert.Equal(TagTaxonomy.GroupArtStyle, _panel.GroupsBeforePalette[0].GroupName);
        Assert.Equal(TagTaxonomy.GroupFileFormat, _panel.GroupsBeforePalette[1].GroupName);
        Assert.Equal(4, _panel.GroupsAfterPalette.Count);
        Assert.Equal(
            [TagTaxonomy.GroupComposition, TagTaxonomy.GroupLighting,
             TagTaxonomy.GroupTheme, TagTaxonomy.GroupCondition],
            _panel.GroupsAfterPalette.Select(g => g.GroupName));

        Assert.Equal(TagTaxonomy.TagsByGroup[TagTaxonomy.GroupArtStyle].Count,
            _panel.GroupsBeforePalette[0].Chips.Count);
        Assert.Equal(TagTaxonomy.TagsByGroup[TagTaxonomy.GroupFileFormat].Count,
            _panel.GroupsBeforePalette[1].Chips.Count);
        Assert.Equal(FilterGroupsPanelViewModel.PaletteHexes.Count,
            _panel.Palette.Swatches.Count);
    }

    [Fact]
    public void Toggle_art_style_chip_writes_and_clears_filter()
    {
        FilterTagChipViewModel chip = _panel.GroupsBeforePalette[0].Chips.First(c => c.Tag == "写实");

        chip.SelectCommand.Execute(null);

        Assert.True(chip.IsActive);
        Assert.NotNull(_gallery.Filter.ArtStyleTags);
        Assert.Equal(["写实"], _gallery.Filter.ArtStyleTags);

        // 再点同一 chip 取消选中 → 字段回到 null（维度不激活）。
        chip.SelectCommand.Execute(null);

        Assert.False(chip.IsActive);
        Assert.Null(_gallery.Filter.ArtStyleTags);
    }

    [Fact]
    public void Toggle_chips_in_two_groups_sets_both_fields_for_and_semantics()
    {
        FilterTagChipViewModel style = _panel.GroupsBeforePalette[0].Chips[0];
        FilterTagChipViewModel comp = _panel.GroupsAfterPalette[0].Chips[0];

        style.SelectCommand.Execute(null);
        comp.SelectCommand.Execute(null);

        Assert.Equal([style.Tag], _gallery.Filter.ArtStyleTags);
        Assert.Equal([comp.Tag], _gallery.Filter.CompositionTags);
    }

    [Fact]
    public void Toggle_format_chip_writes_file_formats()
    {
        FilterTagChipViewModel png = _panel.GroupsBeforePalette[1].Chips.First(c => c.Tag == "PNG");

        png.SelectCommand.Execute(null);

        Assert.Equal(["PNG"], _gallery.Filter.FileFormats);
    }

    [Fact]
    public void Palette_multi_select_writes_color_palette_hexes()
    {
        ColorSwatchViewModel first = _panel.Palette.Swatches[0];
        ColorSwatchViewModel neutral = _panel.Palette.Swatches[^1];

        first.SelectCommand.Execute(null);
        neutral.SelectCommand.Execute(null);

        Assert.True(first.IsActive);
        Assert.True(neutral.IsActive);
        Assert.Equal([first.Hex, neutral.Hex], _gallery.Filter.ColorPaletteHexes);
    }

    [Fact]
    public void ResetSelection_clears_all_groups_and_palette()
    {
        _panel.GroupsBeforePalette[0].Chips[0].SelectCommand.Execute(null);
        _panel.GroupsAfterPalette[0].Chips[0].SelectCommand.Execute(null);
        _panel.Palette.Swatches[0].SelectCommand.Execute(null);
        Assert.NotNull(_gallery.Filter.ArtStyleTags);
        Assert.NotNull(_gallery.Filter.CompositionTags);
        Assert.NotNull(_gallery.Filter.ColorPaletteHexes);

        _panel.ResetSelection();

        Assert.Null(_gallery.Filter.ArtStyleTags);
        Assert.Null(_gallery.Filter.CompositionTags);
        Assert.Null(_gallery.Filter.LightingTags);
        Assert.Null(_gallery.Filter.ThemeTags);
        Assert.Null(_gallery.Filter.ConditionTags);
        Assert.Null(_gallery.Filter.FileFormats);
        Assert.Null(_gallery.Filter.ColorPaletteHexes);
        Assert.All(_panel.GroupsBeforePalette.SelectMany(g => g.Chips), c => Assert.False(c.IsActive));
        Assert.All(_panel.GroupsAfterPalette.SelectMany(g => g.Chips), c => Assert.False(c.IsActive));
        Assert.All(_panel.Palette.Swatches, s => Assert.False(s.IsActive));
    }

    [Fact]
    public void IsExpanded_persists_via_settings()
    {
        Assert.False(_panel.IsExpanded); // 默认收起
        _panel.IsExpanded = true;
        Assert.True(_settings.SidebarFilterGroupsExpanded);
    }

    public void Dispose() => _lib.Dispose();
}
