using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>B1-1 library tree (全部/10 categories/未分类) and B1-3 top tags.</summary>
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
        foreach (string category in Categories.All)
            CategoryNodes.Add(new CategoryNodeViewModel(category, category, false, SelectNode));
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
        for (int i = 0; i < Categories.All.Count; i++)
        {
            string category = Categories.All[i];
            CategoryNodes[i + 1].Count =
                counts.FirstOrDefault(c => c.Category == category)?.Count ?? 0;
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
        _ = _gallery.RefreshAsync();
    }

    private void SelectTag(TagNodeViewModel node)
    {
        // Clicking the active tag again clears the tag filter.
        _gallery.Filter.Tag =
            _gallery.Filter.Tag == node.Tag ? null : node.Tag;
        SyncSelectionState();
        _ = _gallery.RefreshAsync();
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
    }

    public void ResetSelection()
    {
        SyncSelectionState();
    }

    public void RaiseClearFilters() => ClearFiltersRequested?.Invoke();
}

/// <summary>B1-1 tree node.</summary>
public sealed class CategoryNodeViewModel : ObservableObject
{
    private int _count;
    private bool _isSelected;

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
