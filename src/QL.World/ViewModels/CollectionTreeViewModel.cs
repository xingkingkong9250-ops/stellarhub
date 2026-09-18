using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>B1-2 收藏夹树：列出全部收藏夹，单击切换画廊筛选，再次单击清除。</summary>
public sealed class CollectionTreeViewModel
{
    private readonly GalleryViewModel _gallery;
    private readonly CollectionService _collections;

    public CollectionTreeViewModel(GalleryViewModel gallery, CollectionService collections)
    {
        _gallery = gallery;
        _collections = collections;
    }

    public ObservableCollection<CollectionNodeViewModel> CollectionNodes { get; } = [];

    /// <summary>从数据库重新加载节点，保留当前选中状态以避免闪烁。</summary>
    public Task RefreshAsync()
    {
        IReadOnlyList<CollectionCount> rows = _collections.GetAll();
        long? activeId = _gallery.Filter.CollectionId;

        CollectionNodes.Clear();
        foreach (CollectionCount row in rows)
        {
            bool isSelected = row.Id == activeId;
            CollectionNodes.Add(new CollectionNodeViewModel(
                row.Id,
                row.Name,
                row.ImageCount,
                row.CoverHash,
                isSelected,
                SelectNode));
        }

        SyncSelectionState();
        return Task.CompletedTask;
    }

    /// <summary>选中某个收藏夹；再次点击同一个则清除筛选（与 TagTree 行为一致）。</summary>
    private void SelectNode(CollectionNodeViewModel node)
    {
        if (_gallery.Filter.CollectionId == node.Id)
            _gallery.Filter.CollectionId = null;
        else
            _gallery.Filter.CollectionId = node.Id;

        SyncSelectionState();
        _ = _gallery.RefreshAsync();
    }

    /// <summary>根据 Gallery.Filter.CollectionId 重新同步各节点的 IsSelected。</summary>
    public void SyncSelectionState()
    {
        long? activeId = _gallery.Filter.CollectionId;
        foreach (CollectionNodeViewModel node in CollectionNodes)
            node.IsSelected = node.Id == activeId;
    }

    /// <summary>清除筛选时调用，使全部节点恢复为未选中状态。</summary>
    public void ResetSelection()
    {
        foreach (CollectionNodeViewModel node in CollectionNodes)
            node.IsSelected = false;
    }
}

/// <summary>B1-2 收藏夹节点。</summary>
public sealed class CollectionNodeViewModel : ObservableObject
{
    private bool _isSelected;

    public CollectionNodeViewModel(
        long id,
        string displayName,
        int count,
        string? coverHash,
        bool isSelected,
        Action<CollectionNodeViewModel> select)
    {
        Id = id;
        DisplayName = displayName;
        Count = count;
        CoverHash = coverHash;
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public long Id { get; }
    public string DisplayName { get; }
    public int Count { get; }
    public string? CoverHash { get; }
    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
