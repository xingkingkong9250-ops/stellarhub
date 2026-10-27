using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using TinyPinyin;

namespace QL.World.Data;

/// <summary>
/// Forward-only schema migration keyed on PRAGMA user_version.
/// Each migration is applied inside a transaction exactly once and
/// must be idempotent (IF NOT EXISTS).
/// </summary>
internal static class DbMigrator
{
    private const int CURRENT_VERSION = 13;

    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int version = ReadUserVersion(connection);
        if (version >= CURRENT_VERSION)
            return;

        for (int target = version + 1; target <= CURRENT_VERSION; target++)
        {
            string sql = LoadSchemaSql(target);
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();

                // U3-A: v5 adds images.color_family — backfill from existing
                // dominant_colors JSON in the same transaction (pure HSV math).
                if (target == 5)
                    BackfillColorFamilies(connection, transaction);

                // M11-A: v9 adds the library-managed storage columns
                // (library_hash / library_path / original_path /
                // entry_code) and the CAS + entry-code-counter tables.
                // Backfill library_hash ← file_hash and original_path ←
                // file_path so every pre-M11 row points at the software
                // library without a costly copy here; the M11-E wizard
                // fills library_path / entry_code and moves bytes.
                if (target == 9)
                    BackfillLibraryHash(connection, transaction);

                // M12-C: v10 adds legacy_primary_category (backup of old 10
                // categories) and maps them to the new 6 categories. Images
                // with tags get their category re-derived by TaxonomyService.
                if (target == 10)
                    BackfillLegacyCategory(connection, transaction);

                // v0.14.6: v11 adds pinyin column to image_tags and backfills
                // all existing tags with NPinyin conversion so SQL LIKE can
                // match pinyin-based queries.
                if (target == 11)
                    BackfillTagPinyin(connection, transaction);

                command.CommandText = $"PRAGMA user_version = {target};";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>
    /// Derives color_family from the first dominant color for rows that already
    /// have dominant_colors. Rows without colors (or with corrupt JSON) keep a
    /// NULL family — the color filter simply never matches them.
    /// </summary>
    private static void BackfillColorFamilies(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(long Id, string Json)>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id, dominant_colors FROM images
                WHERE color_family IS NULL
                  AND dominant_colors IS NOT NULL AND dominant_colors != '';
                """;
            using var reader = query.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach ((long id, string json) in rows)
        {
            string? family;
            try
            {
                var colors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                family = colors is { Count: > 0 }
                    ? Services.Colors.ColorFamily.HexToFamily(colors[0])
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                continue; // corrupt JSON must never abort the migration
            }
            if (family is null)
                continue;

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE images SET color_family = $family WHERE id = $id;";
            update.Parameters.AddWithValue("$family", family);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Fills library_hash ← file_hash and original_path ← file_path for
    /// pre-M11 rows. Physical files are not copied here — the M11-E
    /// migration wizard handles that (source=local rows are copied +
    /// hardlinked, source=remote rows are skipped + logged per the NAS
    /// bypass constraint). library_path and entry_code stay NULL until
    /// the wizard runs. A single UPDATE is atomic and safe; file_hash is
    /// NOT NULL in the v1 schema so every row qualifies.
    /// </summary>
    private static void BackfillLibraryHash(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE images
            SET library_hash = file_hash,
                original_path = file_path
            WHERE library_hash IS NULL;
            """;
        update.ExecuteNonQuery();
    }

    /// <summary>
    /// M12-C (v0.13.0): 三步迁移旧 10 分类 → 新 6 分类。
    /// 1) 备份：legacy_primary_category ← primary_category（全量）
    /// 2) 映射：按映射表 UPDATE primary_category（5 个有干净映射的旧分类）
    /// 3) 重推导：对有标签的图（无论是否被映射），读标签调 TaxonomyService
    ///    重新推导。无标签的图保持映射后的值或 NULL。
    /// </summary>
    private static void BackfillLegacyCategory(SqliteConnection connection, SqliteTransaction transaction)
    {
        // 1) 备份旧值
        using (var backup = connection.CreateCommand())
        {
            backup.Transaction = transaction;
            backup.CommandText = """
                UPDATE images
                SET legacy_primary_category = primary_category
                WHERE legacy_primary_category IS NULL;
                """;
            backup.ExecuteNonQuery();
        }

        // 2) 映射有干净对应的 5 个旧分类
        var mappings = new (string Old, string New)[]
        {
            ("贴图/纹理", "素材"),
            ("材质参考", "质感"),
            ("环境/场景", "场景"),
            ("雕刻参考", "角色"),
            ("UI/HUD", "道具"),
        };
        foreach ((string old, string @new) in mappings)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE images
                SET primary_category = $new
                WHERE legacy_primary_category = $old;
                """;
            cmd.Parameters.AddWithValue("$new", @new);
            cmd.Parameters.AddWithValue("$old", old);
            cmd.ExecuteNonQuery();
        }

        // 没有干净映射的旧分类（实拍参考/概念设计/灯光参考/配色参考/动画参考）
        // 清空为 NULL，让 AI 重分析或用户重标。
        var unmapped = new[]
        {
            "实拍参考", "概念设计", "灯光参考", "配色参考", "动画参考",
        };
        foreach (string old in unmapped)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE images
                SET primary_category = NULL
                WHERE legacy_primary_category = $old;
                """;
            cmd.Parameters.AddWithValue("$old", old);
            cmd.ExecuteNonQuery();
        }

        // 3) 对有标签的图重推导分类（TaxonomyService 从标签推导新分类）
        var taggedRows = new List<(long Id, List<string> Tags)>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT i.id, t.tag
                FROM images i
                INNER JOIN image_tags t ON t.image_id = i.id
                ORDER BY i.id, t.tag;
                """;
            using var reader = query.ExecuteReader();
            long currentId = -1;
            var currentTags = new List<string>();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string tag = reader.GetString(1);
                if (id != currentId)
                {
                    if (currentTags.Count > 0)
                        taggedRows.Add((currentId, currentTags));
                    currentId = id;
                    currentTags = [];
                }
                currentTags.Add(tag);
            }
            if (currentTags.Count > 0)
                taggedRows.Add((currentId, currentTags));
        }

        foreach ((long id, List<string> tags) in taggedRows)
        {
            string? derived = Services.TaxonomyService.DeriveCategory(tags);
            // 推导成功才覆盖（NULL 推导不覆盖映射后的值）
            if (derived is not null)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE images
                    SET primary_category = $cat, updated_at = $now
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$cat", derived);
                update.Parameters.AddWithValue("$now", DbTime.Now());
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// v0.14.6: 回填 image_tags.pinyin —— 遍历所有标签用 NPinyin 转换后 UPDATE。
    /// 中文标签转拼音（例如「角色」→「juese」），纯英文/数字标签保持原样。
    /// </summary>
    private static void BackfillTagPinyin(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(long ImageId, string Tag)>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT image_id, tag FROM image_tags WHERE pinyin IS NULL;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE image_tags SET pinyin = $pinyin WHERE image_id = $id AND tag = $tag;";
        update.Parameters.AddWithValue("$pinyin", string.Empty);
        update.Parameters.AddWithValue("$id", 0L);
        update.Parameters.AddWithValue("$tag", string.Empty);

        foreach ((long imageId, string tag) in rows)
        {
            string pinyin = PinyinHelper.GetPinyin(tag);
            update.Parameters["$pinyin"].Value = pinyin;
            update.Parameters["$id"].Value = imageId;
            update.Parameters["$tag"].Value = tag;
            update.ExecuteNonQuery();
        }
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    internal static string LoadSchemaSql(int version)
    {
        Assembly assembly = typeof(DbMigrator).Assembly;
        string resourceName = $"QL.World.Data.Schema.v{version}.sql";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded schema resource missing: {resourceName}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
