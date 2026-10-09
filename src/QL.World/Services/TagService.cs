using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// Tag mutations with the manual-protection contract:
/// an existing manual row is never downgraded/overwritten by AI writes,
/// and an AI row can be upgraded to manual when the user adds the same tag.
/// </summary>
public sealed class TagService
{
    private readonly DatabaseService _db;

    public TagService(DatabaseService db) => _db = db;

    /// <summary>
    /// v0.10.0: 用户手动新增标签后触发（AI 写入不触发）。
    /// AiLearningService 订阅记录编辑事件（训练数据留存 + 词表学习）。
    /// </summary>
    public event Action<long, string, TagSource>? TagAdded;

    /// <summary>
    /// v0.10.0: 标签被移除后触发（携带被移除行的来源）。
    /// AiLearningService 订阅记录编辑事件（保留率统计 + 困难负样本）。
    /// </summary>
    public event Action<long, string, TagSource>? TagRemoved;

    /// <summary>
    /// Adds tags to one or more images. Returns the number of newly
    /// created rows (pre-existing protected rows are not counted).
    /// </summary>
    public int AddTags(
        IReadOnlyCollection<long> imageIds,
        IEnumerable<string> tags,
        TagSource source,
        double? confidence = null)
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        ArgumentNullException.ThrowIfNull(tags);

        return AddTags(
            imageIds,
            NormalizeTags(tags).Select(tag => (tag, confidence)),
            source);
    }

    /// <summary>
    /// Adds tags with per-tag confidence values to one or more images.
    /// Manual rows remain protected from AI updates.
    /// </summary>
    public int AddTags(
        IReadOnlyCollection<long> imageIds,
        IEnumerable<(string Tag, double? Confidence)> tags,
        TagSource source)
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        ArgumentNullException.ThrowIfNull(tags);

        List<(string Tag, double? Confidence)> normalized = NormalizeTagConfidences(tags);
        if (imageIds.Count == 0 || normalized.Count == 0)
            return 0;

        string sourceText = ToDb(source);
        string now = DbTime.Now();

        return _db.RunInTransaction((conn, tx) =>
        {
            int created = 0;
            foreach (long imageId in imageIds)
            {
                foreach ((string tag, double? tagConfidence) in normalized)
                    created += UpsertTag(conn, tx, imageId, tag, sourceText, tagConfidence, now);

                // v0.10.0: 仅用户手动写入触发事件（AI 写入走建议快照）。
                if (source == TagSource.Manual && TagAdded is not null)
                    foreach ((string tag, _) in normalized)
                        TagAdded.Invoke(imageId, tag, source);
            }
            return created;
        });
    }

    private static int UpsertTag(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long imageId,
        string tag,
        string source,
        double? confidence,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO image_tags (image_id, tag, source, confidence, created_at)
            VALUES ($id, $tag, $source, $confidence, $now)
            ON CONFLICT(image_id, tag) DO UPDATE SET
                source = CASE
                    WHEN image_tags.source = 'manual' THEN 'manual'
                    ELSE excluded.source END,
                confidence = CASE
                    WHEN image_tags.source = 'manual' THEN image_tags.confidence
                    ELSE excluded.confidence END
            WHERE image_tags.source <> 'manual' OR excluded.source = 'manual';
            """;
        command.Parameters.AddWithValue("$id", imageId);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddNullable("$confidence", confidence);
        command.Parameters.AddWithValue("$now", now);
        return command.ExecuteNonQuery();
    }

    public void RemoveTag(long imageId, string tag)
    {
        string normalized = NormalizeTag(tag);
        TagSource removedSource = _db.Run(conn =>
        {
            // v0.10.0: 先读被移除行的来源（manual/ai），删除后记录编辑事件。
            string source = "ai";
            using (var query = conn.CreateCommand())
            {
                query.CommandText = "SELECT source FROM image_tags WHERE image_id = $id AND tag = $tag;";
                query.Parameters.AddWithValue("$id", imageId);
                query.Parameters.AddWithValue("$tag", normalized);
                object? result = query.ExecuteScalar();
                if (result is string s) source = s;
            }

            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM image_tags WHERE image_id = $id AND tag = $tag;";
            command.Parameters.AddWithValue("$id", imageId);
            command.Parameters.AddWithValue("$tag", normalized);
            command.ExecuteNonQuery();
            return source == "manual" ? TagSource.Manual : TagSource.Ai;
        });
        TagRemoved?.Invoke(imageId, normalized, removedSource);
    }

    /// <summary>Deletes a tag from every image. Returns affected image count.</summary>
    public int DeleteTagEverywhere(string tag)
    {
        string normalized = NormalizeTag(tag);
        List<(long ImageId, TagSource Source)> affected = _db.Run(conn =>
        {
            // v0.10.0: 删除前先取受影响行（image_id + 来源），逐图记录编辑事件。
            var rows = new List<(long, TagSource)>();
            using (var query = conn.CreateCommand())
            {
                query.CommandText = "SELECT image_id, source FROM image_tags WHERE tag = $tag;";
                query.Parameters.AddWithValue("$tag", normalized);
                using var reader = query.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetInt64(0),
                        reader.GetString(1) == "manual" ? TagSource.Manual : TagSource.Ai));
            }

            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM image_tags WHERE tag = $tag;";
            command.Parameters.AddWithValue("$tag", normalized);
            command.ExecuteNonQuery();
            return rows;
        });

        if (TagRemoved is not null)
            foreach ((long imageId, TagSource source) in affected)
                TagRemoved.Invoke(imageId, normalized, source);
        return affected.Count;
    }

    public IReadOnlyList<ImageTagRecord> GetTagsForImage(long imageId) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT image_id, tag, source, confidence, created_at
                FROM image_tags
                WHERE image_id = $id
                ORDER BY CASE source WHEN 'manual' THEN 0 ELSE 1 END, tag;
                """;
            command.Parameters.AddWithValue("$id", imageId);
            using var reader = command.ExecuteReader();

            var result = new List<ImageTagRecord>();
            while (reader.Read())
                result.Add(ReadTag(reader));
            return result;
        });

    /// <summary>
    /// M12 (v0.13.0): 用户手动加/删标签后调用——读取该图全部标签，
    /// 用 TaxonomyService 重新推导 primary_category 并无条件写入。
    /// 不走 AddTags（避免触发 TagAdded 事件循环）。
    /// </summary>
    public void RecomputeCategoryForImage(long imageId)
    {
        IReadOnlyList<ImageTagRecord> tags = GetTagsForImage(imageId);
        string? derived = TaxonomyService.DeriveCategory(
            tags.Select(t => t.Tag).ToList());
        _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE images
                SET primary_category = $cat, updated_at = $now
                WHERE id = $id;
                """;
            cmd.Parameters.AddNullable("$cat", derived);
            cmd.Parameters.AddWithValue("$now", DbTime.Now());
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>B1-3: most used tags with counts.</summary>
    public IReadOnlyList<TagCount> GetTopTags(int limit = 50) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT tag, COUNT(*) AS n
                FROM image_tags
                GROUP BY tag
                ORDER BY n DESC, tag ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();

            var result = new List<TagCount>();
            while (reader.Read())
            {
                result.Add(new TagCount(
                    reader.GetString(0),
                    reader.GetInt32(1)));
            }
            return result;
        });

    internal static string NormalizeTag(string tag)
    {
        string trimmed = tag.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Tag must not be empty.", nameof(tag));
        return trimmed;
    }

    /// <summary>Splits pasted multi-tag input on , ， 、 ; ； and newlines.</summary>
    internal static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string[] parts = raw.Split(
                [',', '，', '、', ';', '；', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string part in parts)
            {
                if (!string.IsNullOrWhiteSpace(part))
                    result.Add(part.Trim());
            }
        }
        return [.. result.OrderBy(t => t, StringComparer.Ordinal)];
    }

    internal static List<(string Tag, double? Confidence)> NormalizeTagConfidences(
        IEnumerable<(string Tag, double? Confidence)> tags)
    {
        var result = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach ((string rawTag, double? confidence) in tags)
        {
            if (string.IsNullOrWhiteSpace(rawTag))
                continue;

            foreach (string tag in NormalizeTags([rawTag]))
                result.TryAdd(tag, confidence);
        }

        return result
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value))
            .ToList();
    }

    internal static string ToDb(TagSource source) =>
        source == TagSource.Manual ? "manual" : "ai";

    internal static TagSource FromDb(string value) =>
        value == "ai" ? TagSource.Ai : TagSource.Manual;

    internal static ImageTagRecord ReadTag(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        FromDb(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetDouble(3),
        DbTime.Parse(reader.GetString(4)));
}
