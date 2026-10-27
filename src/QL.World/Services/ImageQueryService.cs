using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using TinyPinyin;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Colors;

namespace QL.World.Services;

/// <summary>
/// Read model queries, metadata edits and session-scoped undo for removals.
/// All filters are combined with AND; every value is bound as a parameter.
/// </summary>
public sealed class ImageQueryService
{
    private readonly DatabaseService _db;
    private readonly Func<bool>? _isLegacyHidden;

    /// <param name="isLegacyHidden">
    /// M11: when the predicate returns true (W7 chose 跳过 AND W3/A1
    /// 「显示未托管图」is off), gallery queries exclude rows whose bytes
    /// have not been migrated into the managed library
    /// (library_path IS NULL). Null/default = no filtering.
    /// </param>
    public ImageQueryService(DatabaseService db, Func<bool>? isLegacyHidden = null)
    {
        _db = db;
        _isLegacyHidden = isLegacyHidden;
    }

    public int TotalCount() => _db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM images;";
        return Convert.ToInt32(command.ExecuteScalar());
    });

    /// <summary>W3/A5 statistics: rows flagged as missing on disk.</summary>
    public int MissingCount() => _db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM images WHERE is_missing = 1;";
        return Convert.ToInt32(command.ExecuteScalar());
    });

    public ImageRecord? GetById(long id) => _db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT * FROM images WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImage(reader) : null;
    });

    public IReadOnlyList<GalleryListItem> QueryGallery(LibraryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        bool legacyHidden = _isLegacyHidden?.Invoke() == true;

        return _db.Run(conn =>
        {
            var sql = new StringBuilder("""
                SELECT i.id, i.file_name, i.file_path, i.file_hash, i.file_size,
                       i.width, i.height, i.format, i.rating, i.primary_category,
                       i.is_missing, i.created_at,
                       (SELECT GROUP_CONCAT(tag, ',') FROM (
                           SELECT tag FROM image_tags
                           WHERE image_id = i.id
                           ORDER BY CASE source WHEN 'manual' THEN 0 ELSE 1 END, tag
                       )) AS tags_csv,
                       CASE
                           WHEN e.source = 'cloud' THEN '云端'
                           WHEN e.source = 'local' THEN '本地'
                           WHEN EXISTS (SELECT 1 FROM image_tags it2
                                        WHERE it2.image_id = i.id AND it2.source = 'ai') THEN 'AI'
                           ELSE NULL
                       END AS source_badge,
                       i.source,
                       i.nas_connection_id
                FROM images i
                LEFT JOIN image_embeddings e ON e.image_id = i.id
                """);

            var parameters = new List<(string Name, object? Value)>();
            AppendClauses(sql, parameters, filter, legacyHidden);
            sql.Append(' ').Append(GetOrderBy(filter.Sort));
            if (filter.Limit is int limit)
            {
                // Server-side pagination (M4-D). ORDER BY always carries an id
                // tie-break, so OFFSET pages are stable across queries.
                sql.Append("\nLIMIT $limit OFFSET $offset");
                parameters.Add(("$limit", limit));
                parameters.Add(("$offset", filter.Offset));
            }

            using var command = conn.CreateCommand();
            command.CommandText = sql.ToString();
            foreach ((string name, object? value) in parameters)
                command.Parameters.AddNullable(name, value);

            using var reader = command.ExecuteReader();
            var result = new List<GalleryListItem>();
            while (reader.Read())
            {
                result.Add(new GalleryListItem
                {
                    Id = reader.GetInt64(0),
                    FileName = reader.GetString(1),
                    FilePath = reader.GetString(2),
                    FileHash = reader.GetString(3),
                    FileSize = reader.GetInt64(4),
                    Width = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    Height = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    Format = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Rating = reader.GetInt32(8),
                    PrimaryCategory = reader.IsDBNull(9) ? null : reader.GetString(9),
                    IsMissing = reader.GetInt32(10) != 0,
                    CreatedAt = DbTime.Parse(reader.GetString(11)),
                    TagsCsv = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                    SourceBadge = reader.IsDBNull(13) ? null : reader.GetString(13),
                    // M5-E: surface source/nas_connection_id so GalleryThumbnailProvider
                    // can build the right IImageSource (Local vs Remote) per row.
                    Source = reader.GetString(14) == "remote" ? ImageSource.Remote : ImageSource.Local,
                    NasConnectionId = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Filtered row count without loading rows (M4-D pagination support):
    /// same WHERE clauses as <see cref="QueryGallery"/>, no ORDER BY/LIMIT.
    /// </summary>
    public int CountFiltered(LibraryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        bool legacyHidden = _isLegacyHidden?.Invoke() == true;

        return _db.Run(conn =>
        {
            var sql = new StringBuilder("SELECT COUNT(*)\nFROM images i");
            var parameters = new List<(string Name, object? Value)>();
            AppendClauses(sql, parameters, filter, legacyHidden);

            using var command = conn.CreateCommand();
            command.CommandText = sql.ToString();
            foreach ((string name, object? value) in parameters)
                command.Parameters.AddNullable(name, value);
            return Convert.ToInt32(command.ExecuteScalar());
        });
    }

    /// <summary>B1-1 counts per category; null category is 未分类.</summary>
    public IReadOnlyList<CategoryCount> GetCategoryCounts() => _db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT primary_category, COUNT(*)
            FROM images
            GROUP BY primary_category;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<CategoryCount>();
        while (reader.Read())
        {
            result.Add(new CategoryCount(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt32(1)));
        }
        return result;
    });

    // ─── Metadata edits ────────────────────────────────────────────────────

    public void UpdateRating(long id, int rating)
    {
        int clamped = Math.Clamp(rating, 0, 5);
        MutateField("rating", id, clamped, "UPDATE images SET rating = $v, updated_at = $now WHERE id = $id;");
    }

    public void SetRatingBatch(IReadOnlyCollection<long> ids, int rating)
    {
        int clamped = Math.Clamp(rating, 0, 5);
        BatchMutate(ids, "UPDATE images SET rating = $v, updated_at = $now WHERE id = $id;", clamped);
    }

    public void UpdateNotes(long id, string notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        MutateField("notes", id, notes, "UPDATE images SET notes = $v, updated_at = $now WHERE id = $id;");
    }

    public void UpdateCategory(long id, string? category)
    {
        string? normalized = Categories.IsKnown(category) ? category : null;
        MutateField("category", id, (object?)normalized,
            "UPDATE images SET primary_category = $v, updated_at = $now WHERE id = $id;");
    }

    public void SetCategoryBatch(IReadOnlyCollection<long> ids, string? category)
    {
        string? normalized = Categories.IsKnown(category) ? category : null;
        BatchMutate(ids,
            "UPDATE images SET primary_category = $v, updated_at = $now WHERE id = $id;",
            (object?)normalized);
    }

    public void SetMissing(long id, bool isMissing, string? newPath = null)
    {
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                UPDATE images
                SET is_missing = $missing,
                    file_path  = COALESCE($path, file_path),
                    file_name  = CASE WHEN $path IS NULL THEN file_name ELSE $name END,
                    sortable_name = CASE WHEN $path IS NULL THEN sortable_name ELSE $sortable END,
                    updated_at = $now
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$missing", isMissing ? 1 : 0);
            command.Parameters.AddNullable("$path", newPath);
            command.Parameters.AddNullable("$name",
                newPath is null ? null : Path.GetFileName(newPath));
            command.Parameters.AddNullable("$sortable",
                newPath is null ? null : Path.GetFileName(newPath).ToLowerInvariant());
            command.Parameters.AddWithValue("$now", DbTime.Now());
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// B3-2 重新定位：将缺失图片重新指向新路径，重新计算 hash。
    /// 若新文件的内容 hash 在库中已存在（属于其它图片记录）则拒绝并返回 false。
    /// 否则更新路径/文件名/排序名/hash/大小并清除缺失标记后返回 true。
    /// </summary>
    public bool RelocateFile(long id, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        string hash = Md5Hasher.ComputeFile(newPath);
        long size = new FileInfo(newPath).Length;
        string fileName = Path.GetFileName(newPath);
        string sortable = fileName.ToLowerInvariant();

        return _db.RunInTransaction((conn, tx) =>
        {
            using (var conflict = conn.CreateCommand())
            {
                conflict.Transaction = tx;
                conflict.CommandText = """
                    SELECT COUNT(*) FROM images
                    WHERE file_hash = $hash AND id != $id;
                    """;
                conflict.Parameters.AddWithValue("$hash", hash);
                conflict.Parameters.AddWithValue("$id", id);
                int duplicates = Convert.ToInt32(conflict.ExecuteScalar());
                if (duplicates > 0)
                    return false;
            }

            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                UPDATE images
                SET file_path = $path,
                    file_name = $name,
                    sortable_name = $sortable,
                    file_hash = $hash,
                    file_size = $size,
                    is_missing = 0,
                    updated_at = $now
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$path", newPath);
            command.Parameters.AddWithValue("$name", fileName);
            command.Parameters.AddWithValue("$sortable", sortable);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$now", DbTime.Now());
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            return true;
        });
    }

    /// <summary>Finds an image id by exact file path, or null if not in library.</summary>
    public long? FindByPath(string filePath)
    {
        return _db.Run<long?>(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT id FROM images WHERE file_path = $path;";
            command.Parameters.AddWithValue("$path", filePath);
            object? result = command.ExecuteScalar();
            return result is null or DBNull ? (long?)null : Convert.ToInt64(result);
        });
    }

    // ─── Removal with session undo ────────────────────────────────────────

    /// <summary>
    /// Removes records (never touches disk files) and returns a restorable
    /// bundle including every tag row (CASCADE would otherwise drop them).
    /// </summary>
    public RemovedEntryBundle Remove(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return _db.RunInTransaction((conn, tx) =>
        {
            var images = new List<ImageRecord>();
            var tags = new List<ImageTagRecord>();

            foreach (long id in ids)
            {
                using (var query = conn.CreateCommand())
                {
                    query.Transaction = tx;
                    query.CommandText = "SELECT * FROM images WHERE id = $id;";
                    query.Parameters.AddWithValue("$id", id);
                    using var reader = query.ExecuteReader();
                    if (reader.Read())
                        images.Add(ReadImage(reader));
                }

                using (var tagQuery = conn.CreateCommand())
                {
                    tagQuery.Transaction = tx;
                    tagQuery.CommandText =
                        "SELECT image_id, tag, source, confidence, created_at FROM image_tags WHERE image_id = $id;";
                    tagQuery.Parameters.AddWithValue("$id", id);
                    using var reader = tagQuery.ExecuteReader();
                    while (reader.Read())
                        tags.Add(TagService.ReadTag(reader));
                }
            }

            foreach (long id in ids)
            {
                // 0.16.0 DUP-D: duplicate_candidates 对 images(id) 有外键且无
                // ON DELETE CASCADE（schema 只增不删），必须先清候选行。
                using (var candidates = conn.CreateCommand())
                {
                    candidates.Transaction = tx;
                    candidates.CommandText =
                        "DELETE FROM duplicate_candidates WHERE image_id = $id;";
                    candidates.Parameters.AddWithValue("$id", id);
                    candidates.ExecuteNonQuery();
                }

                using var delete = conn.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM images WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            return new RemovedEntryBundle(images, tags);
        });
    }

    /// <summary>
    /// Restores a removed bundle. Rows whose file_path was re-imported in
    /// the meantime are skipped to avoid unique-constraint failures.
    /// </summary>
    public int Restore(RemovedEntryBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        return _db.RunInTransaction((conn, tx) =>
        {
            int restored = 0;
            foreach (ImageRecord image in bundle.Images)
            {
                long newId;
                using (var check = conn.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = "SELECT id FROM images WHERE file_path = $path;";
                    check.Parameters.AddWithValue("$path", image.FilePath);
                    var existing = check.ExecuteScalar();
                    if (existing is not null)
                        continue;
                }

                using (var insert = conn.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO images
                            (id, file_path, file_name, sortable_name, file_hash, file_size,
                             width, height, format, rating, notes, primary_category,
                             dominant_colors, is_missing, created_at, updated_at)
                        VALUES
                            ($id, $path, $name, $sortable, $hash, $size,
                             $width, $height, $format, $rating, $notes, $category,
                             $colors, $missing, $created, $updated);
                        """;
                    insert.Parameters.AddWithValue("$id", image.Id);
                    insert.Parameters.AddWithValue("$path", image.FilePath);
                    insert.Parameters.AddWithValue("$name", image.FileName);
                    insert.Parameters.AddWithValue("$sortable", image.SortableName);
                    insert.Parameters.AddWithValue("$hash", image.FileHash);
                    insert.Parameters.AddWithValue("$size", image.FileSize);
                    insert.Parameters.AddNullable("$width", image.Width);
                    insert.Parameters.AddNullable("$height", image.Height);
                    insert.Parameters.AddNullable("$format", image.Format);
                    insert.Parameters.AddWithValue("$rating", image.Rating);
                    insert.Parameters.AddWithValue("$notes", image.Notes);
                    insert.Parameters.AddNullable("$category", image.PrimaryCategory);
                    insert.Parameters.AddNullable("$colors", image.DominantColorsJson);
                    insert.Parameters.AddWithValue("$missing", image.IsMissing ? 1 : 0);
                    insert.Parameters.AddWithValue("$created", DbTime.Format(image.CreatedAt));
                    insert.Parameters.AddWithValue("$updated", DbTime.Format(image.UpdatedAt));
                    insert.ExecuteNonQuery();
                }

                newId = image.Id;
                restored++;

                foreach (ImageTagRecord tag in bundle.Tags.Where(t => t.ImageId == image.Id))
                {
                    using var tagInsert = conn.CreateCommand();
                    tagInsert.Transaction = tx;
                    tagInsert.CommandText = """
                        INSERT OR IGNORE INTO image_tags (image_id, tag, source, confidence, created_at, pinyin)
                        VALUES ($id, $tag, $source, $confidence, $created, $pinyin);
                        """;
                    tagInsert.Parameters.AddWithValue("$id", newId);
                    tagInsert.Parameters.AddWithValue("$tag", tag.Tag);
                    tagInsert.Parameters.AddWithValue("$source", TagService.ToDb(tag.Source));
                    tagInsert.Parameters.AddNullable("$confidence", tag.Confidence);
                    tagInsert.Parameters.AddWithValue("$created", DbTime.Format(tag.CreatedAt));
                    tagInsert.Parameters.AddWithValue("$pinyin", PinyinHelper.GetPinyin(tag.Tag));
                    tagInsert.ExecuteNonQuery();
                }
            }
            return restored;
        });
    }

    // ─── Internals ─────────────────────────────────────────────────────────

    private void MutateField(string name, long id, object? value, string sql)
    {
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddNullable("$v", value);
            command.Parameters.AddWithValue("$now", DbTime.Now());
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    private void BatchMutate(IReadOnlyCollection<long> ids, string sql, object? value)
    {
        if (ids.Count == 0)
            return;

        _db.RunInTransaction((conn, tx) =>
        {
            foreach (long id in ids)
            {
                using var command = conn.CreateCommand();
                command.Transaction = tx;
                command.CommandText = sql;
                command.Parameters.AddNullable("$v", value);
                command.Parameters.AddWithValue("$now", DbTime.Now());
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            return true;
        });
    }

    private static void AppendClauses(
        StringBuilder sql,
        List<(string Name, object? Value)> parameters,
        LibraryFilter filter,
        bool legacyHidden)
    {
        // M11: soft-skip of unmigrated legacy rows (W7 「跳过」). Applied
        // first so it also constrains the semantic-result early returns.
        if (legacyHidden)
            sql.Append("\nWHERE i.library_path IS NOT NULL");

        // Semantic results take precedence: restrict to pre-computed id list.
        // Other filters (Text/Tag/Category) are intentionally ignored — the
        // semantic query already defines the result set.
        if (filter.SemanticResultIds is { Count: > 0 } ids)
        {
            sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
            var placeholders = new List<string>(ids.Count);
            int i = 0;
            foreach (long id in ids)
            {
                string name = $"$sid{i++}";
                placeholders.Add(name);
                parameters.Add((name, id));
            }
            sql.Append($"i.id IN ({string.Join(", ", placeholders)})");
            return;
        }

        if (filter.SearchMode == SearchMode.Semantic)
        {
            // Semantic mode active but no result ids (e.g., AI not configured,
            // empty library, or EmbedTextAsync returned null). Yield zero rows
            // so the UI shows "no matches" instead of all images.
            sql.Append(HasWhere(sql) ? "\n  AND 1=0" : "\nWHERE 1=0");
            return;
        }

        // U2-B (§4.B.1): 最近入库 — restrict to images created within last N days.
        // created_at is UTC ISO-8601 ("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"); same-format
        // cutoff string compares lexicographically correctly.
        if (filter.ImportedWithinDays is int days && days > 0)
        {
            sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
            sql.Append("i.created_at >= $cutoff");
            parameters.Add(("$cutoff", DbTime.Format(DateTime.UtcNow.AddDays(-days))));
        }

        if (filter.OnlyMissing)
            sql.Append("\nWHERE i.is_missing = 1");

        if (filter.Uncategorized)
        {
            sql.Append(filter.OnlyMissing ? "\n  AND " : "\nWHERE ");
            sql.Append("i.primary_category IS NULL");
        }
        else if (!string.IsNullOrEmpty(filter.Category))
        {
            sql.Append(filter.OnlyMissing ? "\n  AND " : "\nWHERE ");
            sql.Append("i.primary_category = $category");
            parameters.Add(("$category", filter.Category));
        }

        if (!string.IsNullOrEmpty(filter.Tag))
        {
            sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
            sql.Append("""
                EXISTS (SELECT 1 FROM image_tags it
                        WHERE it.image_id = i.id AND it.tag = $tag)
                """);
            parameters.Add(("$tag", filter.Tag));
        }

        // M12-E (§4.B.1): 标签组筛选。组内任一标签命中即通过（OR），
        // 组与组之间 AND。美术风格/画面构图/灯光氛围/题材气质/状态形容；
        // v0.13.7 起质感（原 B1-1 分类）作为 B1-6-11 标签组同构参与。
        AppendTagGroupClause(sql, parameters, "$artStyle", filter.ArtStyleTags);
        AppendTagGroupClause(sql, parameters, "$composition", filter.CompositionTags);
        AppendTagGroupClause(sql, parameters, "$lighting", filter.LightingTags);
        AppendTagGroupClause(sql, parameters, "$theme", filter.ThemeTags);
        AppendTagGroupClause(sql, parameters, "$condition", filter.ConditionTags);
        AppendTagGroupClause(sql, parameters, "$texture", filter.TextureTags);

        // M12-E: 文件格式筛选（与 i.format 不区分大小写比对）。
        if (filter.FileFormats is { Count: > 0 } formats)
        {
            var placeholders = new List<string>(formats.Count);
            for (int i = 0; i < formats.Count; i++)
                placeholders.Add($"$fmt{i}");
            sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
            sql.Append($"LOWER(i.format) IN ({string.Join(", ", placeholders)})");
            for (int i = 0; i < formats.Count; i++)
                parameters.Add((placeholders[i], formats[i].ToLowerInvariant()));
        }

        // M12-E: 色彩倾向——色板 hex 映射为色族后匹配 color_family。
        // 全部 hex 无法解析时忽略该筛选（与 U3-A「未知桶键忽略」一致）。
        if (filter.ColorPaletteHexes is { Count: > 0 } hexes)
        {
            List<string> families = hexes
                .Select(ColorFamily.HexToFamily)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (families.Count > 0)
            {
                var placeholders = new List<string>(families.Count);
                for (int i = 0; i < families.Count; i++)
                    placeholders.Add($"$pf{i}");
                sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
                sql.Append($"i.color_family IN ({string.Join(", ", placeholders)})");
                for (int i = 0; i < families.Count; i++)
                    parameters.Add((placeholders[i], families[i]));
            }
        }

        if (filter.CollectionId is long collectionId)
        {
            sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
            sql.Append("""
                EXISTS (SELECT 1 FROM collection_images ci
                        WHERE ci.image_id = i.id AND ci.collection_id = $collection)
                """);
            parameters.Add(("$collection", collectionId));
        }

        if (filter.PbrGroupId is long pbrGroupId)
        {
            sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
            sql.Append("""
                EXISTS (SELECT 1 FROM pbr_group_images pgi
                        WHERE pgi.image_id = i.id AND pgi.group_id = $pbrGroup)
                """);
            parameters.Add(("$pbrGroup", pbrGroupId));
        }

        // B1-5 attribute filters. Unknown bucket keys are ignored.
        // （v0.13.4 起色族筛选由 M12-E 色板子句 ColorPaletteHexes 统一承担）
        if (!string.IsNullOrEmpty(filter.ResolutionBucket))
        {
            // Long edge MAX(width,height); NULL dimensions match no bucket.
            string? clause = filter.ResolutionBucket switch
            {
                ResolutionBuckets.Lt1024 =>
                    "MAX(i.width, i.height) < 1024",
                ResolutionBuckets.From1024To2047 =>
                    "MAX(i.width, i.height) BETWEEN 1024 AND 2047",
                ResolutionBuckets.From2048To4095 =>
                    "MAX(i.width, i.height) BETWEEN 2048 AND 4095",
                ResolutionBuckets.Ge4096 =>
                    "MAX(i.width, i.height) >= 4096",
                _ => null,
            };
            if (clause is not null)
            {
                sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
                sql.Append("(i.width IS NOT NULL AND i.height IS NOT NULL AND ")
                    .Append(clause).Append(')');
            }
        }

        if (!string.IsNullOrEmpty(filter.FileSizeBucket))
        {
            const long mb = 1024L * 1024L;
            string? clause = filter.FileSizeBucket switch
            {
                FileSizeBuckets.Lt1Mb => $"i.file_size < {mb}",
                FileSizeBuckets.From1To5Mb => $"i.file_size BETWEEN {mb} AND {5 * mb - 1}",
                FileSizeBuckets.From5To20Mb => $"i.file_size BETWEEN {5 * mb} AND {20 * mb - 1}",
                FileSizeBuckets.Ge20Mb => $"i.file_size >= {20 * mb}",
                _ => null,
            };
            if (clause is not null)
            {
                sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
                sql.Append(clause);
            }
        }

        if (!string.IsNullOrEmpty(filter.RatingBucket))
        {
            string? clause = filter.RatingBucket switch
            {
                RatingBuckets.Unrated => "i.rating = 0",
                RatingBuckets.Ge3 => "i.rating >= 3",
                RatingBuckets.Ge4 => "i.rating >= 4",
                RatingBuckets.Eq5 => "i.rating = 5",
                _ => null,
            };
            if (clause is not null)
            {
                sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
                sql.Append(clause);
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");

            // v0.14.6: 智能搜索 — 多字段 OR 联合查询
            // Tag 模式（默认）：标签 + 文件名 + 备注 + 分类 + 拼音（如果有）
            // FileNameNotes 模式：文件名 + 备注 + 拼音
            if (filter.SearchMode == SearchMode.Tag)
            {
                sql.Append("""
                    (EXISTS (SELECT 1 FROM image_tags it
                            WHERE it.image_id = i.id
                              AND it.tag LIKE '%' || $text || '%')
                     OR i.file_name LIKE '%' || $text || '%'
                     OR i.notes LIKE '%' || $text || '%'
                     OR i.primary_category LIKE '%' || $text || '%')
                    """);
                if (!string.IsNullOrWhiteSpace(filter.SearchPinyin))
                {
                    sql.Append("""
                     OR EXISTS (SELECT 1 FROM image_tags it2
                               WHERE it2.image_id = i.id
                                 AND it2.pinyin LIKE '%' || $pinyin || '%')
                     OR i.file_name LIKE '%' || $pinyin || '%'
                    """);
                    parameters.Add(("$pinyin", filter.SearchPinyin));
                }
            }
            else
            {
                sql.Append("(i.file_name LIKE '%' || $text || '%' OR i.notes LIKE '%' || $text || '%')");
                if (!string.IsNullOrWhiteSpace(filter.SearchPinyin))
                {
                    sql.Append(" OR i.file_name LIKE '%' || $pinyin || '%'");
                    parameters.Add(("$pinyin", filter.SearchPinyin));
                }
            }
            parameters.Add(("$text", filter.Text.Trim()));
        }
    }

    /// <summary>M12-E: one tag-group clause — OR within the group (tag IN (...)).</summary>
    private static void AppendTagGroupClause(
        StringBuilder sql,
        List<(string Name, object? Value)> parameters,
        string prefix,
        IReadOnlyList<string>? tags)
    {
        if (tags is not { Count: > 0 })
            return;

        var placeholders = new List<string>(tags.Count);
        for (int i = 0; i < tags.Count; i++)
            placeholders.Add($"{prefix}{i}");

        sql.Append(HasWhere(sql) ? "\n  AND " : "\nWHERE ");
        sql.Append($"""
            EXISTS (SELECT 1 FROM image_tags it
                    WHERE it.image_id = i.id AND it.tag IN ({string.Join(", ", placeholders)}))
            """);
        for (int i = 0; i < tags.Count; i++)
            parameters.Add((placeholders[i], tags[i]));
    }

    private static bool HasWhere(StringBuilder sql) =>
        sql.ToString().Contains("\nWHERE", StringComparison.Ordinal);

    private static string GetOrderBy(GallerySort sort) => sort switch
    {
        GallerySort.ImportedAsc => "ORDER BY i.created_at ASC, i.id ASC",
        GallerySort.NameAsc => "ORDER BY i.sortable_name ASC, i.id ASC",
        GallerySort.RatingDesc => "ORDER BY i.rating DESC, i.created_at DESC",
        GallerySort.SizeDesc => "ORDER BY i.file_size DESC, i.id DESC",
        _ => "ORDER BY i.created_at DESC, i.id DESC",
    };

    internal static ImageRecord ReadImage(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        FilePath = reader.GetString(reader.GetOrdinal("file_path")),
        FileName = reader.GetString(reader.GetOrdinal("file_name")),
        SortableName = reader.GetString(reader.GetOrdinal("sortable_name")),
        FileHash = reader.GetString(reader.GetOrdinal("file_hash")),
        FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
        Width = reader.IsDBNull(reader.GetOrdinal("width")) ? null : reader.GetInt32(reader.GetOrdinal("width")),
        Height = reader.IsDBNull(reader.GetOrdinal("height")) ? null : reader.GetInt32(reader.GetOrdinal("height")),
        Format = reader.IsDBNull(reader.GetOrdinal("format")) ? null : reader.GetString(reader.GetOrdinal("format")),
        Rating = reader.GetInt32(reader.GetOrdinal("rating")),
        Notes = reader.GetString(reader.GetOrdinal("notes")),
        PrimaryCategory = reader.IsDBNull(reader.GetOrdinal("primary_category"))
            ? null : reader.GetString(reader.GetOrdinal("primary_category")),
        DominantColorsJson = reader.IsDBNull(reader.GetOrdinal("dominant_colors"))
            ? null : reader.GetString(reader.GetOrdinal("dominant_colors")),
        IsMissing = reader.GetInt32(reader.GetOrdinal("is_missing")) != 0,
        CreatedAt = DbTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DbTime.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        // M5-G: surface source/nas_connection_id on GetById so the download
        // queue can route Local (File.Copy) vs Remote (INasConnector).
        Source = reader.GetString(reader.GetOrdinal("source")) == "remote"
            ? ImageSource.Remote : ImageSource.Local,
        NasConnectionId = reader.IsDBNull(reader.GetOrdinal("nas_connection_id"))
            ? null : reader.GetInt32(reader.GetOrdinal("nas_connection_id")),
    };
}

/// <summary>Session-scoped restorable bundle from a remove operation.</summary>
public sealed record RemovedEntryBundle(
    IReadOnlyList<ImageRecord> Images,
    IReadOnlyList<ImageTagRecord> Tags);
