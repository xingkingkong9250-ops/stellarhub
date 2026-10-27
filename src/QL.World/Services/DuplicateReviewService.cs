using QL.World.Data;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>0.16.0 DUP-D: W8 组列表的类型筛选。</summary>
public enum DuplicateKindFilter
{
    /// <summary>全部重复组。</summary>
    All,
    /// <summary>严格同图（组内全部 L1）。</summary>
    Exact,
    /// <summary>近似变体（组内含 L2/L3 成员）。</summary>
    Variant,
}

/// <summary>0.16.0 DUP-D: W8 组列表的状态筛选。</summary>
public enum DuplicateStatusFilter
{
    /// <summary>待处理。</summary>
    Pending,
    /// <summary>已处理（resolved 或 dismissed）。</summary>
    Processed,
}

/// <summary>0.16.0 DUP-D: 重复组内的一张图片及其候选信息。</summary>
public sealed record DuplicateMemberRecord(
    string GroupId,
    long ImageId,
    string FileName,
    string FilePath,
    string FileHash,
    long FileSize,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    int Rating,
    string Kind,
    string Level,
    double Score,
    string Status,
    ImageSource Source,
    int? NasConnectionId);

/// <summary>0.16.0 DUP-D: 一个重复组（成员按入库 id 升序）。</summary>
public sealed record DuplicateGroupRecord(
    string GroupId,
    IReadOnlyList<DuplicateMemberRecord> Members)
{
    /// <summary>组内全部成员均为 L1 严格同图时为 exact，否则 variant。</summary>
    public string Kind =>
        Members.All(member => member.Kind == "exact") ? "exact" : "variant";

    /// <summary>组内存在 L3 终审行时标 L3，否则按类型派生 L1/L2。</summary>
    public string Level =>
        Members.Any(member => member.Level == "L3")
            ? "L3"
            : Kind == "exact" ? "L1" : "L2";

    public double Score => Members.Count == 0 ? 0 : Members.Max(member => member.Score);

    public string Status => Members.Count == 0 ? "pending" : Members[0].Status;

    public bool IsPending => Status == "pending";
}

/// <summary>
/// 0.16.0 DUP-D: W8 重复审核窗口的数据服务。
/// 只负责组的读取与状态变更（SQLite 事务），不引用任何 UI 类型。
/// </summary>
public sealed class DuplicateReviewService
{
    private readonly DatabaseService _db;

    public DuplicateReviewService(DatabaseService db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// 读取候选组，按类型/状态筛选。状态按组判定（组内行状态一致；
    /// 删除成员后剩余的 pending 行仍属同一组）。
    /// </summary>
    public List<DuplicateGroupRecord> ListGroups(
        DuplicateKindFilter kind = DuplicateKindFilter.All,
        DuplicateStatusFilter status = DuplicateStatusFilter.Pending)
    {
        List<DuplicateMemberRecord> rows = _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT dc.group_id, dc.image_id, dc.kind, dc.level, dc.score, dc.status,
                       i.file_name, i.file_path, i.file_hash, i.file_size,
                       i.width, i.height, i.created_at, i.rating,
                       i.source, i.nas_connection_id
                FROM duplicate_candidates dc
                JOIN images i ON i.id = dc.image_id
                ORDER BY dc.group_id, i.id;
                """;

            var list = new List<DuplicateMemberRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DuplicateMemberRecord(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    DbTime.Parse(reader.GetString(12)),
                    reader.GetInt32(13),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.GetString(5),
                    reader.IsDBNull(14) ? ImageSource.Local : MapSource(reader.GetString(14)),
                    reader.IsDBNull(15) ? null : reader.GetInt32(15)));
            }
            return list;
        });

        var groups = new List<DuplicateGroupRecord>();
        foreach (IGrouping<string, DuplicateMemberRecord> bucket in rows.GroupBy(row => row.GroupId))
        {
            var group = new DuplicateGroupRecord(
                bucket.Key,
                bucket.OrderBy(member => member.ImageId).ToList());

            bool processed = group.Status is "resolved" or "dismissed";
            if (status == DuplicateStatusFilter.Pending && processed)
                continue;
            if (status == DuplicateStatusFilter.Processed && !processed)
                continue;
            if (kind == DuplicateKindFilter.Exact && group.Kind != "exact")
                continue;
            if (kind == DuplicateKindFilter.Variant && group.Kind != "variant")
                continue;

            groups.Add(group);
        }

        // 待处理按分数降序（最相似的先看）；已处理按组 id 稳定排序。
        groups.Sort((left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0
                ? byScore
                : string.CompareOrdinal(left.GroupId, right.GroupId);
        });
        return groups;
    }

    /// <summary>把整组候选行改为给定状态（dismissed / resolved）。</summary>
    public void SetGroupStatus(string groupId, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        string now = DbTime.Now();
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                UPDATE duplicate_candidates
                SET status = $status, updated_at = $now
                WHERE group_id = $group;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$group", groupId);
            command.ExecuteNonQuery();
        });
    }

    private static ImageSource MapSource(string value) =>
        value == "remote" ? ImageSource.Remote : ImageSource.Local;
}
