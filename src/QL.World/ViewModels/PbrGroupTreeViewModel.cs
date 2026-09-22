using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Pbr;

namespace QL.World.ViewModels;

/// <summary>B1-4 贴图组树：列出全部 PBR 组，单击切换画廊筛选，再次单击清除。</summary>
public sealed class PbrGroupTreeViewModel
{
    private readonly GalleryViewModel _gallery;
    private readonly PbrGroupingService _grouping;

    public PbrGroupTreeViewModel(GalleryViewModel gallery, PbrGroupingService grouping)
    {
        _gallery = gallery;
        _grouping = grouping;
    }

    public ObservableCollection<PbrGroupNodeViewModel> GroupNodes { get; } = [];

    /// <summary>从数据库重新加载节点，保留当前选中状态以避免闪烁。</summary>
    public Task RefreshAsync()
    {
        IReadOnlyList<PbrGroupInfo> rows = _grouping.GetGroups();
        long? activeId = _gallery.Filter.PbrGroupId;

        GroupNodes.Clear();
        foreach (PbrGroupInfo row in rows)
        {
            GroupNodes.Add(new PbrGroupNodeViewModel(
                row.Id,
                row.Name,
                row.MemberCount,
                row.Id == activeId,
                SelectNode));
        }

        SyncSelectionState();
        return Task.CompletedTask;
    }

    /// <summary>选中某个贴图组；再次点击同一个则清除筛选（与收藏夹树行为一致）。</summary>
    private void SelectNode(PbrGroupNodeViewModel node)
    {
        if (_gallery.Filter.PbrGroupId == node.Id)
            _gallery.Filter.PbrGroupId = null;
        else
            _gallery.Filter.PbrGroupId = node.Id;

        SyncSelectionState();
        _ = _gallery.RefreshAsync();
    }

    /// <summary>根据 Gallery.Filter.PbrGroupId 重新同步各节点的 IsSelected。</summary>
    public void SyncSelectionState()
    {
        long? activeId = _gallery.Filter.PbrGroupId;
        foreach (PbrGroupNodeViewModel node in GroupNodes)
            node.IsSelected = node.Id == activeId;
    }

    /// <summary>清除筛选时调用，使全部节点恢复为未选中状态。</summary>
    public void ResetSelection()
    {
        foreach (PbrGroupNodeViewModel node in GroupNodes)
            node.IsSelected = false;
    }
}

/// <summary>B1-4 贴图组节点。</summary>
public sealed class PbrGroupNodeViewModel : ObservableObject
{
    private bool _isSelected;

    public PbrGroupNodeViewModel(
        long id,
        string displayName,
        int memberCount,
        bool isSelected,
        Action<PbrGroupNodeViewModel> select)
    {
        Id = id;
        DisplayName = displayName;
        MemberCount = memberCount;
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public long Id { get; }
    public string DisplayName { get; }
    public int MemberCount { get; }
    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
