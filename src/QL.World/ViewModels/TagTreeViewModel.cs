using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Models.Taxonomy;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// B1-1 library tree (全部/树展示分类/未分类) and B1-3 top tags.
/// M12-F: 分类节点可展开显示该分类组的标签作为二级节点（TagTaxonomy.TagsOfCategory）；
/// 点击二级标签即按单标签筛选（与 B1-3 chips 同语义，Filter.Tag）。
/// v0.13.7: 树展示分类取自 TagTaxonomy.TreeCategories（质感移至 B1-6-11，
/// 不再出现在树中）；全部 6 分类仍参与 DeriveCategory 与持久化。
/// </summary>
public sealed class TagTreeViewModel
{
    private readonly GalleryViewModel _gallery;
    private readonly ImageQueryService _query;
    private readonly TagService _tags;

    public TagTreeViewModel(
        GalleryViewModel gallery,
        ImageQueryService query,
        TagService tags)
    {
        _gallery = gallery;
        _query = query;
        _tags = tags;

        CategoryNodes.Add(new CategoryNodeViewModel("全部", null, uncategorized: false, SelectNode));
        foreach (string category in TagTaxonomy.TreeCategories)
        {
            CategoryNodeViewModel node = new(category, category, false, SelectNode);
            foreach (string tag in TagTaxonomy.TagsOfCategory(category))
                node.Children.Add(new TagNodeViewModel(tag, 0, SelectTag));
            CategoryNodes.Add(node);
        }
        CategoryNodes.Add(new CategoryNodeViewModel("未分类", null, uncategorized: true, SelectNode));
    }

    public ObservableCollection<CategoryNodeViewModel> CategoryNodes { get; } = [];
    public ObservableCollection<TagNodeViewModel> TopTagNodes { get; } = [];

    /// <summary>B1 clear-chip request; wired by the shell to reset every filter.</summary>
    public event Action? ClearFiltersRequested;

    public async Task RefreshCountsAsync()
    {
        IReadOnlyList<CategoryCount> counts =
            await Task.Run(_query.GetCategoryCounts);
        int total = await Task.Run(_query.TotalCount);

        int uncategorized = counts.FirstOrDefault(c => c.Category is null)?.Count ?? 0;

        CategoryNodes[0].Count = total; // 全部
        // v0.13.7: 树节点与分类不再按索引一一对应（质感已移出树），按分类名映射计数。
        foreach (CategoryNodeViewModel node in CategoryNodes)
        {
            if (node.Category is null)
                continue; // 全部 / 未分类
            node.Count = counts.FirstOrDefault(c => c.Category == node.Category)?.Count ?? 0;
        }
        CategoryNodes[^1].Count = uncategorized; // 未分类

        SyncSelectionState();
        await RefreshTopTagsAsync();
    }

    public async Task RefreshTopTagsAsync()
    {
        IReadOnlyList<TagCount> tags = await Task.Run(() => _tags.GetTopTags(50));

        TopTagNodes.Clear();
        foreach (TagCount tag in tags)
            TopTagNodes.Add(new TagNodeViewModel(tag.Tag, tag.Count, SelectTag));
    }

    private void SelectNode(CategoryNodeViewModel node)
    {
        if (node.IsUncategorized)
        {
            _gallery.Filter.Uncategorized = true;
            _gallery.Filter.Category = null;
        }
        else if (node.Category is null) // 全部
        {
            _gallery.Filter.Uncategorized = false;
            _gallery.Filter.Category = null;
        }
        else
        {
            _gallery.Filter.Uncategorized = false;
            _gallery.Filter.Category = node.Category;
        }

        SyncSelectionState();
        _ = RefreshGallerySafe();
    }

    private void SelectTag(TagNodeViewModel node)
    {
        // Clicking the active tag again clears the tag filter.
        _gallery.Filter.Tag =
            _gallery.Filter.Tag == node.Tag ? null : node.Tag;
        SyncSelectionState();
        _ = RefreshGallerySafe();
    }

    /// <summary>
    /// F-10: fire-and-forget refresh with exception swallowing. A failed
    /// RefreshAsync must never leave the gallery stuck empty — the user
    /// sees the old items rather than a blank grid.
    /// </summary>
    private async Task RefreshGallerySafe()
    {
        try
        {
            await _gallery.RefreshAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TagTree] gallery refresh failed after filter change: {ex.Message}");
        }
    }

    private void SyncSelectionState()
    {
        foreach (CategoryNodeViewModel node in CategoryNodes)
        {
            node.IsSelected =
                _gallery.Filter.Uncategorized
                    ? node.IsUncategorized
                    : string.IsNullOrEmpty(_gallery.Filter.Category)
                        ? node.Category is null && !node.IsUncategorized
                        : node.Category == _gallery.Filter.Category;
        }

        foreach (TagNodeViewModel node in TopTagNodes)
            node.IsActive = _gallery.Filter.Tag == node.Tag;

        // M12-F: 二级标签节点与 B1-3 chips 共用 Filter.Tag，活跃态一并同步。
        foreach (CategoryNodeViewModel category in CategoryNodes)
            foreach (TagNodeViewModel child in category.Children)
                child.IsActive = _gallery.Filter.Tag == child.Tag;
    }

    public void ResetSelection()
    {
        SyncSelectionState();
    }

    public void RaiseClearFilters() => ClearFiltersRequested?.Invoke();
}

/// <summary>B1-1 tree node. M12-F: 可展开显示该分类组的标签作为二级节点。</summary>
public sealed class CategoryNodeViewModel : ObservableObject
{
    private int _count;
    private bool _isSelected;
    private bool _isExpanded;

    public CategoryNodeViewModel(
        string displayName,
        string? category,
        bool uncategorized,
        Action<CategoryNodeViewModel> select)
    {
        DisplayName = displayName;
        Category = category;
        IsUncategorized = uncategorized;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string DisplayName { get; }
    public string? Category { get; }
    public bool IsUncategorized { get; }
    public RelayCommand SelectCommand { get; }

    /// <summary>二级标签节点（分类组的标签词表；全部/未分类为空）。</summary>
    public ObservableCollection<TagNodeViewModel> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    /// <summary>二级展开态；默认收起，箭头 ToggleButton TwoWay 绑定。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>B1-3 high-frequency tag chip.</summary>
public sealed class TagNodeViewModel : ObservableObject
{
    private bool _isActive;

    public TagNodeViewModel(string tag, int count, Action<TagNodeViewModel> select)
    {
        Tag = tag;
        Count = count;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Tag { get; }
    public int Count { get; }
    public RelayCommand SelectCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
