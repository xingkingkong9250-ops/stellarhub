using QL.World.Models.Taxonomy;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>
/// M12-F: B1-1 树结构 — 全部 + 6 新分类（可展开二级标签）+ 未分类；
/// 分类/二级标签点击写 Filter；计数按新分类刷新。
/// </summary>
public sealed class TagTreeViewModelM12Tests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly AppSettingsService _settings;
    private readonly GalleryViewModel _gallery;
    private readonly TagTreeViewModel _tree;

    public TagTreeViewModelM12Tests()
    {
        _settings = new AppSettingsService(_lib.DirectoryRoot);
        _settings.Load();
        _gallery = new GalleryViewModel(
            new ImageQueryService(_lib.Db),
            _settings,
            new GalleryThumbnailProvider(new ThumbnailService(_lib.DirectoryRoot, _settings)));
        _tree = new TagTreeViewModel(
            _gallery, new ImageQueryService(_lib.Db), new TagService(_lib.Db));
    }

    [Fact]
    public void Tree_builds_all_plus_six_taxonomy_categories_plus_uncategorized()
    {
        Assert.Equal(TagTaxonomy.Categories.Count + 2, _tree.CategoryNodes.Count);
        Assert.Equal("全部", _tree.CategoryNodes[0].DisplayName);
        for (int i = 0; i < TagTaxonomy.Categories.Count; i++)
            Assert.Equal(TagTaxonomy.Categories[i], _tree.CategoryNodes[i + 1].DisplayName);
        Assert.Equal("未分类", _tree.CategoryNodes[^1].DisplayName);
        Assert.False(_tree.CategoryNodes[0].HasChildren);
        Assert.False(_tree.CategoryNodes[^1].HasChildren);
    }

    [Fact]
    public void Category_nodes_expose_taxonomy_tags_as_children()
    {
        CategoryNodeViewModel character = _tree.CategoryNodes[1]; // 角色
        Assert.True(character.HasChildren);
        Assert.Equal(TagTaxonomy.TagsOfCategory(TagTaxonomy.Character), character.Children.Select(c => c.Tag));

        CategoryNodeViewModel creature = _tree.CategoryNodes[2]; // 生物
        Assert.Equal(TagTaxonomy.TagsOfCategory(TagTaxonomy.Creature).Count, creature.Children.Count);
    }

    [Fact]
    public void Child_tag_click_toggles_single_tag_filter()
    {
        CategoryNodeViewModel character = _tree.CategoryNodes[1];
        TagNodeViewModel child = character.Children.First(c => c.Tag == "少女");

        child.SelectCommand.Execute(null);

        Assert.Equal("少女", _gallery.Filter.Tag);
        Assert.True(child.IsActive);

        child.SelectCommand.Execute(null);

        Assert.Null(_gallery.Filter.Tag);
        Assert.False(child.IsActive);
    }

    [Fact]
    public async Task RefreshCountsAsync_counts_by_new_categories()
    {
        _lib.InsertImage(category: TagTaxonomy.Character);
        _lib.InsertImage(category: TagTaxonomy.Scene);

        await _tree.RefreshCountsAsync();

        Assert.Equal(2, _tree.CategoryNodes[0].Count); // 全部
        Assert.Equal(1, _tree.CategoryNodes[1].Count); // 角色
        Assert.Equal(1, _tree.CategoryNodes[3].Count); // 场景
        Assert.Equal(0, _tree.CategoryNodes[^1].Count); // 未分类
    }

    public void Dispose() => _lib.Dispose();
}
