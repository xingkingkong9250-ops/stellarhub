using System.IO;
using QL.World.Models;
using QL.World.Models.Taxonomy;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>
/// M12 补齐：B3 详情栏手动加/删标签后 primary_category 必须按
/// TaxonomyService 重推导（RecomputeCategoryForImage 接入契约），
/// 以及分类下拉/右键菜单换为标签驱动的 6 个新分类。
/// </summary>
public sealed class DetailCategoryRecomputeTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly GalleryViewModel _gallery;
    private readonly TagService _tags;
    private readonly ImageQueryService _query;
    private readonly DetailViewModel _detail;

    public DetailCategoryRecomputeTests()
    {
        var settings = new AppSettingsService(_lib.DirectoryRoot);
        settings.Load();
        var thumbnails = new ThumbnailService(_lib.DirectoryRoot, settings);
        _query = new ImageQueryService(_lib.Db);
        _tags = new TagService(_lib.Db);
        _gallery = new GalleryViewModel(_query, settings, new GalleryThumbnailProvider(thumbnails));
        _detail = new DetailViewModel(
            _query, _tags, _gallery,
            new SemanticSearchService(_lib.Db, new FakeAiService()));
    }

    [Fact]
    public void CategoryOptions_uses_six_taxonomy_categories_plus_uncategorized()
    {
        Assert.Equal(TagTaxonomy.Categories.Count + 1, _detail.CategoryOptions.Count);
        Assert.Equal("未分类", _detail.CategoryOptions[0].Display);
        Assert.Null(_detail.CategoryOptions[0].Value);
        Assert.Equal(TagTaxonomy.Categories, _detail.CategoryOptions.Skip(1).Select(o => o.Value));
        // 旧 10 分类名不得再出现。
        Assert.DoesNotContain(_detail.CategoryOptions, o => o.Value == Categories.Texture);
        Assert.DoesNotContain(_detail.CategoryOptions, o => o.Value == Categories.ConceptDesign);
    }

    [Fact]
    public async Task AddTag_with_category_group_tag_recomputes_primary_category()
    {
        long id = Insert("knight.png");
        Assert.Null(_query.GetById(id)!.PrimaryCategory);

        await SelectSingleAsync(id);
        _detail.NewTagText = "骑士"; // 角色组标签
        _detail.AddTagCommand.Execute(null);

        Assert.Equal(TagTaxonomy.Character, _query.GetById(id)!.PrimaryCategory);
        Assert.Equal(string.Empty, _detail.NewTagText);
    }

    [Fact]
    public async Task AddTag_with_filter_only_tag_leaves_category_null()
    {
        long id = Insert("style.png");

        await SelectSingleAsync(id);
        _detail.NewTagText = "写实"; // 美术风格筛选组标签，不参与分类推导
        _detail.AddTagCommand.Execute(null);

        Assert.Null(_query.GetById(id)!.PrimaryCategory);
    }

    [Fact]
    public async Task RemoveTag_recomputes_category_back_to_null()
    {
        long id = Insert("knight.png");
        // 造初始态：手动标签「骑士」→ 推导为角色。
        _tags.AddTags([id], ["骑士"], TagSource.Manual);
        _tags.RecomputeCategoryForImage(id);
        Assert.Equal(TagTaxonomy.Character, _query.GetById(id)!.PrimaryCategory);

        await SelectSingleAsync(id);
        _detail.RemoveTagCommand.Execute(new DetailTagViewModel("骑士", false, _detail.RemoveTagCommand));

        Assert.Null(_query.GetById(id)!.PrimaryCategory);
    }

    [Fact]
    public async Task BatchAddTags_recomputes_category_for_each_image()
    {
        long a = Insert("forest1.png");
        long b = Insert("forest2.png");

        await SelectBatchAsync(a, b);
        _detail.BatchAddTagsText = "森林"; // 场景组标签
        _detail.BatchAddTagsCommand.Execute(null);

        Assert.Equal(TagTaxonomy.Scene, _query.GetById(a)!.PrimaryCategory);
        Assert.Equal(TagTaxonomy.Scene, _query.GetById(b)!.PrimaryCategory);
    }

    [Fact]
    public async Task BatchRemoveTags_recomputes_each_image_once_after_all_deletions()
    {
        long a = Insert("a.png");
        long b = Insert("b.png");
        foreach (long id in new[] { a, b })
        {
            _tags.AddTags([id], ["骑士", "写实"], TagSource.Manual);
            _tags.RecomputeCategoryForImage(id);
        }
        Assert.Equal(TagTaxonomy.Character, _query.GetById(a)!.PrimaryCategory);

        await SelectBatchAsync(a, b);
        _detail.BatchRemoveTagsText = "骑士";
        _detail.BatchRemoveTagsCommand.Execute(null);

        // 分类组标签被删，仅剩筛选组标签「写实」→ 回落未分类。
        Assert.Null(_query.GetById(a)!.PrimaryCategory);
        Assert.Null(_query.GetById(b)!.PrimaryCategory);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private long Insert(string fileName) =>
        _lib.InsertImage(filePath: Path.Combine(_lib.DirectoryRoot, fileName));

    private async Task SelectSingleAsync(long id)
    {
        await _gallery.RefreshAsync();
        GalleryItemViewModel item = _gallery.Items.First(i => i.Id == id);
        _gallery.SelectedItems.Clear();
        _gallery.SelectedItems.Add(item);
        _gallery.NotifySelectionChanged();
        for (int i = 0; i < 200 && _detail.FileName != item.FileName; i++)
            await Task.Delay(10);
        Assert.Equal(item.FileName, _detail.FileName);
    }

    private async Task SelectBatchAsync(params long[] ids)
    {
        await _gallery.RefreshAsync();
        _gallery.SelectedItems.Clear();
        foreach (long id in ids)
            _gallery.SelectedItems.Add(_gallery.Items.First(i => i.Id == id));
        _gallery.NotifySelectionChanged();
        for (int i = 0; i < 200 && !_detail.IsBatch; i++)
            await Task.Delay(10);
        Assert.True(_detail.IsBatch, "多选未进入批量模式");
    }

    public void Dispose() => _lib.Dispose();
}
