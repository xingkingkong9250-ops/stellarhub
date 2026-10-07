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
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM image_tags WHERE image_id = $id AND tag = $tag;";
            command.Parameters.AddWithValue("$id", imageId);
            command.Parameters.AddWithValue("$tag", normalized);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Deletes a tag from every image. Returns affected image count.</summary>
    public int DeleteTagEverywhere(string tag)
    {
        string normalized = NormalizeTag(tag);
        return _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM image_tags WHERE tag = $tag;";
            command.Parameters.AddWithValue("$tag", normalized);
            return command.ExecuteNonQuery();
        });
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
