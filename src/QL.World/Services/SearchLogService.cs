using System.Text.Json;
using Microsoft.Data.Sqlite;
using QL.World.Data;

namespace QL.World.Services;

/// <summary>
/// 搜索词条日志服务 — 将用户的 chip 搜索词条落库。
/// 仅在画廊命中非空时写入，用于后续打标词表补充和 AI 学习数据。
/// </summary>
public sealed class SearchLogService
{
    private readonly DatabaseService _db;

    public SearchLogService(DatabaseService db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// 写入一次搜索日志。terms 至少一项且 resultCount > 0 时才落库；
    /// 空词条或零命中静默忽略。
    /// </summary>
    public void Insert(IReadOnlyList<string> terms, int resultCount, string? userId = null)
    {
        if (terms is not { Count: > 0 } || resultCount <= 0)
            return;

        // 过滤空白 + 去重 + 保持顺序
        var cleaned = new List<string>(terms.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string t in terms)
        {
            string trimmed = t.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
                continue;
            cleaned.Add(trimmed);
        }
        if (cleaned.Count == 0)
            return;

        string json = JsonSerializer.Serialize(cleaned);
        string now = Data.DbTime.Now();

        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO search_log (terms_json, result_count, user_id, created_at)
                VALUES ($json, $count, $userId, $now);
                """;
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$count", resultCount);
            cmd.Parameters.AddWithValue("$userId", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        });
    }
}
