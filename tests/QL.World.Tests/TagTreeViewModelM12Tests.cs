using QL.World.Models.Taxonomy;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>
/// M12-F: B1-1 树结构 — 全部 + 树展示分类（可展开二级标签）+ 未分类；
/// 分类/二级标签点击写 Filter；计数按分类刷新。
/// v0.13.7: 质感移出树（TagTaxonomy.TreeCategories，5 个），入口在 B1-6-11。
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
    public void Tree_builds_all_plus_tree_categories_plus_uncategorized()
    {
        Assert.Equal(TagTaxonomy.TreeCategories.Count + 2, _tree.CategoryNodes.Count);
        Assert.Equal("全部", _tree.CategoryNodes[0].DisplayName);
        for (int i = 0; i < TagTaxonomy.TreeCategories.Count; i++)
            Assert.Equal(TagTaxonomy.TreeCategories[i], _tree.CategoryNodes[i + 1].DisplayName);
        Assert.Equal("未分类", _tree.CategoryNodes[^1].DisplayName);
        Assert.False(_tree.CategoryNodes[0].HasChildren);
        Assert.False(_tree.CategoryNodes[^1].HasChildren);
    }

    [Fact]
    public void Tree_excludes_texture_category_after_move_to_filter_groups()
    {
        // v0.13.7: 质感仍是 Categories/推导真源，但树中不再出现（入口在 B1-6-11）。
        Assert.DoesNotContain(_tree.CategoryNodes, n => n.DisplayName == TagTaxonomy.Texture);
        Assert.DoesNotContain(_tree.CategoryNodes, n => n.Category == TagTaxonomy.Texture);
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
    public async Task RefreshCountsAsync_counts_by_tree_categories()
    {
        _lib.InsertImage(category: TagTaxonomy.Character);
        _lib.InsertImage(category: TagTaxonomy.Scene);
        // v0.13.7: 质感图仍计入「全部」，但树无质感节点（计数按名映射，不串位）。
        _lib.InsertImage(category: TagTaxonomy.Texture);

        await _tree.RefreshCountsAsync();

        Assert.Equal(3, _tree.CategoryNodes[0].Count); // 全部（含质感）
        Assert.Equal(1, _tree.CategoryNodes[1].Count); // 角色
        Assert.Equal(1, _tree.CategoryNodes[3].Count); // 场景
        Assert.Equal(0, _tree.CategoryNodes[^1].Count); // 未分类
    }

    public void Dispose() => _lib.Dispose();
}
