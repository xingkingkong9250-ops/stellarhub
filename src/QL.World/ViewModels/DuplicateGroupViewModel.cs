using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// 0.16.0 DUP-D: W8 左侧组卡片与右侧组详情的 VM。
/// 成员按分辨率×大小计算「推荐保留」，组级徽标由候选层级/类型派生。
/// </summary>
public sealed class DuplicateGroupViewModel : ObservableObject
{
    private const int STACK_PREVIEW_COUNT = 3;

    public DuplicateGroupViewModel(DuplicateGroupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;

        DuplicateMemberRecord recommended = SelectRecommended(record.Members);
        Members = new ObservableCollection<DuplicateMemberViewModel>(
            record.Members.Select(member =>
                new DuplicateMemberViewModel(
                    member, member.ImageId == recommended.ImageId)));

        foreach (DuplicateMemberViewModel member in Members)
        {
            member.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DuplicateMemberViewModel.IsSelected))
                    OnPropertyChanged(nameof(HasSelection));
            };
        }

        StackMembers = new ObservableCollection<DuplicateMemberViewModel>(
            Members.Take(STACK_PREVIEW_COUNT));
    }

    public DuplicateGroupRecord Record { get; }
    public ObservableCollection<DuplicateMemberViewModel> Members { get; }

    /// <summary>左侧卡片缩略图堆叠（最多 3 张）。</summary>
    public ObservableCollection<DuplicateMemberViewModel> StackMembers { get; }

    public string GroupId => Record.GroupId;

    public int MemberCount => Members.Count;

    public string MemberCountText => $"{MemberCount} 张";

    public bool IsPending => Record.IsPending;

    public string StatusText => Record.Status switch
    {
        "resolved" => "已处理",
        "dismissed" => "已忽略",
        _ => "待处理",
    };

    public string KindText => Record.Kind == "exact" ? "严格同图" : "近似变体";

    public string LevelText => Record.Level;

    public double Score => Record.Score;

    public string ScoreText => Record.Score.ToString("0.00");

    /// <summary>组内任一成员被勾选时「删除选中」可用。</summary>
    public bool HasSelection => Members.Any(member => member.IsSelected);

    /// <summary>按分辨率（宽×高）优先、文件大小次优挑出推荐保留的一张。</summary>
    private static DuplicateMemberRecord SelectRecommended(
        IReadOnlyList<DuplicateMemberRecord> members) =>
        members
            .OrderByDescending(member => (long)(member.Width ?? 0) * (member.Height ?? 0))
            .ThenByDescending(member => member.FileSize)
            .ThenBy(member => member.ImageId)
            .First();
}
