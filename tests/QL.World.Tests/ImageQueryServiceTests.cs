using System.IO;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Pbr;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class ImageQueryServiceTests
{
    [Fact]
    public void Filters_combine_with_and_and_sort_is_applied_in_sql()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);

        long metalA = library.InsertImage(filePath: @"X:\a.png", category: Categories.MaterialReference, rating: 3);
        long metalB = library.InsertImage(filePath: @"X:\b.png", category: Categories.MaterialReference, rating: 5);
        long concept = library.InsertImage(filePath: @"X:\c.png", category: Categories.ConceptDesign, rating: 5);
        tags.AddTags([metalA, metalB], ["metal"], TagSource.Manual);

        var onlyMetal = query.QueryGallery(new LibraryFilter
        {
            Category = Categories.MaterialReference,
            Tag = "metal",
            Sort = GallerySort.RatingDesc,
        });
        Assert.Equal(2, onlyMetal.Count);
        Assert.Equal(metalB, onlyMetal[0].Id);

        var conceptOnly = query.QueryGallery(new LibraryFilter { Category = Categories.ConceptDesign });
        Assert.Single(conceptOnly);
        Assert.Equal(concept, conceptOnly[0].Id);
    }

    [Fact]
    public void Uncategorized_filter_finds_null_category_rows()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        library.InsertImage(category: null);
        library.InsertImage(category: Categories.UiHud);

        var result = query.QueryGallery(new LibraryFilter { Uncategorized = true });
        Assert.Single(result);
        Assert.Null(result[0].PrimaryCategory);
    }

    [Fact]
    public void Text_search_covers_tag_mode_and_file_notes_mode()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);
        long id = library.InsertImage(filePath: @"X:\dragon_scale.png", notes: "生锈参考");
        tags.AddTags([id], ["dragon"], TagSource.Manual);

        var byTag = query.QueryGallery(new LibraryFilter
        {
            Text = "dra", SearchMode = SearchMode.Tag,
        });
        Assert.Single(byTag);

        var byFileName = query.QueryGallery(new LibraryFilter
        {
            Text = "scale", SearchMode = SearchMode.FileNameNotes,
        });
        Assert.Single(byFileName);

        var byNotes = query.QueryGallery(new LibraryFilter
        {
            Text = "生锈", SearchMode = SearchMode.FileNameNotes,
        });
        Assert.Single(byNotes);
    }

    [Fact]
    public void Pbr_group_filter_restricts_to_group_members()
    {
        using var library = new TestLibrary();
        var settings = new AppSettingsService(library.DirectoryRoot);
        settings.Load();
        var grouping = new PbrGroupingService(library.Db, settings);
        var query = new ImageQueryService(library.Db);

        string dir = Path.Combine(library.DirectoryRoot, "tex");
        Directory.CreateDirectory(dir);
        long color = library.InsertImage(Path.Combine(dir, "T_A_BaseColor.png"));
        long normal = library.InsertImage(Path.Combine(dir, "T_A_Normal.png"));
        long outside = library.InsertImage(Path.Combine(dir, "T_B_BaseColor.png"));

        Assert.Equal(1, grouping.RescanAll().GroupsCreated);

        long groupId = library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT id FROM pbr_groups LIMIT 1;";
            return Convert.ToInt64(command.ExecuteScalar());
        });

        var result = query.QueryGallery(new LibraryFilter { PbrGroupId = groupId });
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == color);
        Assert.Contains(result, r => r.Id == normal);
        Assert.DoesNotContain(result, r => r.Id == outside);
    }

    [Fact]
    public void Category_counts_and_total_track_every_row()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        library.InsertImage(category: Categories.MaterialReference);
        library.InsertImage(category: Categories.MaterialReference);
        library.InsertImage(category: null);

        Assert.Equal(3, query.TotalCount());
        var counts = query.GetCategoryCounts();
        Assert.Equal(2, counts.Single(c => c.Category == Categories.MaterialReference).Count);
        Assert.Equal(1, counts.Single(c => c.Category is null).Count);
    }

    [Fact]
    public void Rating_notes_and_category_updates_persist_and_clamp()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long id = library.InsertImage();

        query.UpdateRating(id, 9);
        query.UpdateNotes(id, "useful");
        query.UpdateCategory(id, Categories.LightingReference);

        var record = query.GetById(id)!;
        Assert.Equal(5, record.Rating);
        Assert.Equal("useful", record.Notes);
        Assert.Equal(Categories.LightingReference, record.PrimaryCategory);

        query.UpdateCategory(id, "not-a-real-category");
        Assert.Null(query.GetById(id)!.PrimaryCategory);
    }

    [Fact]
    public void Remove_keeps_disk_file_but_undo_restores_record_and_tags()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);
        long id = library.InsertImage();
        tags.AddTags([id], ["metal"], TagSource.Manual);

        RemovedEntryBundle bundle = query.Remove([id]);
        Assert.Single(bundle.Images);
        Assert.Single(bundle.Tags);
        Assert.Null(query.GetById(id));

        int restored = query.Restore(bundle);
        Assert.Equal(1, restored);
        Assert.Equal("metal", tags.GetTagsForImage(id).Single().Tag);
    }

    [Fact]
    public void Batch_category_and_rating_apply_to_all_selected()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long a = library.InsertImage();
        long b = library.InsertImage();

        query.SetCategoryBatch([a, b], Categories.Texture);
        query.SetRatingBatch([a, b], 4);

        Assert.Equal(Categories.Texture, query.GetById(a)!.PrimaryCategory);
        Assert.Equal(4, query.GetById(b)!.Rating);
    }

    [Fact]
    public void RelocateFile_updates_path_hash_and_clears_missing()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long id = library.InsertImage(filePath: @"X:\lost.png", hash: "oldhash");
        query.SetMissing(id, isMissing: true);

        string dir = CreateTempDir();
        try
        {
            string newPath = Path.Combine(dir, "found.png");
            File.WriteAllBytes(newPath, [1, 2, 3, 4]);

            Assert.True(query.RelocateFile(id, newPath));

            ImageRecord? record = query.GetById(id);
            Assert.NotNull(record);
            Assert.Equal(newPath, record.FilePath);
            Assert.Equal("found.png", record.FileName);
            Assert.Equal("found.png", record.SortableName);
            Assert.Equal(Md5Hasher.ComputeFile(newPath), record.FileHash);
            Assert.Equal(4, record.FileSize);
            Assert.False(record.IsMissing);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RelocateFile_rejects_content_that_duplicates_another_record()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);

        string dir = CreateTempDir();
        try
        {
            string filePath = Path.Combine(dir, "dupe.png");
            File.WriteAllBytes(filePath, [9, 9, 9]);
            string hash = Md5Hasher.ComputeFile(filePath);

            library.InsertImage(filePath: @"X:\owner.png", hash: hash);
            long missingId = library.InsertImage(filePath: @"X:\lost.png", hash: "oldhash");
            query.SetMissing(missingId, isMissing: true);

            Assert.False(query.RelocateFile(missingId, filePath));

            ImageRecord? record = query.GetById(missingId);
            Assert.NotNull(record);
            Assert.Equal(@"X:\lost.png", record.FilePath);
            Assert.Equal("oldhash", record.FileHash);
            Assert.True(record.IsMissing);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RelocateFile_allows_relocating_a_record_to_its_own_hash()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);

        string dir = CreateTempDir();
        try
        {
            string filePath = Path.Combine(dir, "same.png");
            File.WriteAllBytes(filePath, [7, 7, 7]);
            string hash = Md5Hasher.ComputeFile(filePath);

            long id = library.InsertImage(filePath: @"X:\lost.png", hash: hash);
            query.SetMissing(id, isMissing: true);

            Assert.True(query.RelocateFile(id, filePath));
            Assert.False(query.GetById(id)!.IsMissing);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Limit_and_offset_page_through_results_without_overlap()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        for (int i = 0; i < 5; i++)
            library.InsertImage(filePath: $@"X:\img{i:00}.png");

        var page1 = query.QueryGallery(new LibraryFilter { Limit = 2, Offset = 0 });
        var page2 = query.QueryGallery(new LibraryFilter { Limit = 2, Offset = 2 });
        var page3 = query.QueryGallery(new LibraryFilter { Limit = 2, Offset = 4 });

        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Single(page3);
        // Pages must tile the full sorted result set exactly, in order.
        var all = query.QueryGallery(new LibraryFilter());
        Assert.Equal(
            all.Select(row => row.Id),
            page1.Concat(page2).Concat(page3).Select(row => row.Id));
    }

    [Fact]
    public void CountFiltered_matches_query_row_count_across_filters()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);
        long a = library.InsertImage(category: Categories.Texture);
        library.InsertImage(category: Categories.Texture);
        library.InsertImage(category: null);
        tags.AddTags([a], ["metal"], TagSource.Manual);

        Assert.Equal(3, query.CountFiltered(new LibraryFilter()));
        Assert.Equal(2, query.CountFiltered(new LibraryFilter { Category = Categories.Texture }));
        Assert.Equal(1, query.CountFiltered(new LibraryFilter { Tag = "metal" }));
        Assert.Equal(1, query.CountFiltered(new LibraryFilter { Uncategorized = true }));
        Assert.Equal(0, query.CountFiltered(new LibraryFilter { SearchMode = SearchMode.Semantic }));
    }

    [Fact]
    public void Source_badge_reflects_embedding_source_then_ai_tag_fallback()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);

        long local = library.InsertImage(filePath: @"X:\local.png");
        long cloud = library.InsertImage(filePath: @"X:\cloud.png");
        long aiTagOnly = library.InsertImage(filePath: @"X:\aitag.png");
        long none = library.InsertImage(filePath: @"X:\none.png");

        // embeddings with dim>0 (real vectors); dim=0 marker rows shouldn't badge as "本地".
        InsertEmbedding(library, local, source: "local", dim: 4);
        InsertEmbedding(library, cloud, source: "cloud", dim: 4);
        // aiTagOnly: no embedding, but has an AI-sourced tag → "AI".
        tags.AddTags([aiTagOnly], ["sky"], TagSource.Ai);
        // none: no embedding, no AI tag → null.

        var byId = query.QueryGallery(new LibraryFilter())
            .ToDictionary(r => r.Id, r => r.SourceBadge);

        Assert.Equal("本地", byId[local]);
        Assert.Equal("云端", byId[cloud]);
        Assert.Equal("AI", byId[aiTagOnly]);
        Assert.Null(byId[none]);
    }

    private static void InsertEmbedding(TestLibrary library, long imageId, string source, int dim)
    {
        library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO image_embeddings (image_id, model_id, dim, embedding, created_at, source)
                VALUES ($id, $model, $dim, $blob, $now, $source);
                """;
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.Parameters.AddWithValue("$model", "test");
            cmd.Parameters.AddWithValue("$dim", dim);
            cmd.Parameters.AddWithValue("$blob", new byte[dim * sizeof(float)]);
            cmd.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.000Z");
            cmd.Parameters.AddWithValue("$source", source);
            cmd.ExecuteNonQuery();
        });
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ─── U3-A (§4.B.2): B1-5 attribute filters ─────────────────────────────

    [Fact]
    public void Color_family_filter_matches_only_that_family()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long red = library.InsertImage(filePath: @"X:\red.png");
        long gray = library.InsertImage(filePath: @"X:\gray.png");
        long noColor = library.InsertImage(filePath: @"X:\none.png");
        SetAttributes(library, red, colorFamily: "red");
        SetAttributes(library, gray, colorFamily: "gray");

        var result = query.QueryGallery(new LibraryFilter { ColorFamily = "red" });
        Assert.Single(result);
        Assert.Equal(red, result[0].Id);

        // 未分析颜色（color_family NULL）的图片不匹配任何色族，不报错。
        Assert.Empty(query.QueryGallery(new LibraryFilter { ColorFamily = "blue" }));
    }

    [Fact]
    public void Resolution_bucket_uses_long_edge_and_null_dimensions_match_nothing()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long small = library.InsertImage(filePath: @"X:\s.png");      // 800×600
        long medium = library.InsertImage(filePath: @"X:\m.png");     // 1500×1000
        long large = library.InsertImage(filePath: @"X:\l.png");      // 3000×2000
        long huge = library.InsertImage(filePath: @"X:\h.png");       // 5000×100
        long nullDims = library.InsertImage(filePath: @"X:\n.png");
        SetAttributes(library, small, width: 800, height: 600);
        SetAttributes(library, medium, width: 1500, height: 1000);
        SetAttributes(library, large, width: 3000, height: 2000);
        SetAttributes(library, huge, width: 5000, height: 100);
        // InsertImage 默认 10×10；NULL 尺寸必须显式 UPDATE。
        library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE images SET width = NULL, height = NULL WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", nullDims);
            cmd.ExecuteNonQuery();
        });

        Assert.Equal(small, SingleId(query, ResolutionBuckets.Lt1024));
        Assert.Equal(medium, SingleId(query, ResolutionBuckets.From1024To2047));
        Assert.Equal(large, SingleId(query, ResolutionBuckets.From2048To4095));
        Assert.Equal(huge, SingleId(query, ResolutionBuckets.Ge4096));

        // 边界：长边恰好 1024 落在 1024–2047 档。
        long edge1024 = library.InsertImage(filePath: @"X:\e1.png");
        SetAttributes(library, edge1024, width: 1024, height: 10);
        var bucket2 = query.QueryGallery(
            new LibraryFilter { ResolutionBucket = ResolutionBuckets.From1024To2047 });
        Assert.Equal(2, bucket2.Count);
        Assert.Contains(bucket2, r => r.Id == edge1024);

        static long SingleId(ImageQueryService q, string bucket) =>
            q.QueryGallery(new LibraryFilter { ResolutionBucket = bucket }).Single().Id;
    }

    [Fact]
    public void File_size_bucket_filters_by_byte_ranges()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        const long mb = 1024L * 1024L;
        long tiny = library.InsertImage(filePath: @"X:\t.png");
        long mid = library.InsertImage(filePath: @"X:\m.png");
        long big = library.InsertImage(filePath: @"X:\b.png");
        long huge = library.InsertImage(filePath: @"X:\h.png");
        SetAttributes(library, tiny, fileSize: 512 * 1024);
        SetAttributes(library, mid, fileSize: 3 * mb);
        SetAttributes(library, big, fileSize: 10 * mb);
        SetAttributes(library, huge, fileSize: 25 * mb);

        Assert.Equal(tiny, query.QueryGallery(
            new LibraryFilter { FileSizeBucket = FileSizeBuckets.Lt1Mb }).Single().Id);
        Assert.Equal(mid, query.QueryGallery(
            new LibraryFilter { FileSizeBucket = FileSizeBuckets.From1To5Mb }).Single().Id);
        Assert.Equal(big, query.QueryGallery(
            new LibraryFilter { FileSizeBucket = FileSizeBuckets.From5To20Mb }).Single().Id);
        Assert.Equal(huge, query.QueryGallery(
            new LibraryFilter { FileSizeBucket = FileSizeBuckets.Ge20Mb }).Single().Id);

        // 边界：恰好 1 MB 落在 1–5 MB 档。
        long edge = library.InsertImage(filePath: @"X:\e.png");
        SetAttributes(library, edge, fileSize: mb);
        var bucket2 = query.QueryGallery(
            new LibraryFilter { FileSizeBucket = FileSizeBuckets.From1To5Mb });
        Assert.Equal(2, bucket2.Count);
        Assert.Contains(bucket2, r => r.Id == edge);
    }

    [Fact]
    public void Rating_bucket_filters_and_combines_with_other_filters()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long unrated = library.InsertImage(filePath: @"X:\u.png", rating: 0);
        long three = library.InsertImage(filePath: @"X:\t.png", rating: 3);
        long four = library.InsertImage(filePath: @"X:\f.png", rating: 4);
        long five = library.InsertImage(filePath: @"X:\v.png", rating: 5);

        Assert.Equal(unrated, query.QueryGallery(
            new LibraryFilter { RatingBucket = RatingBuckets.Unrated }).Single().Id);
        Assert.Equal(3, query.QueryGallery(
            new LibraryFilter { RatingBucket = RatingBuckets.Ge3 }).Count);
        Assert.Equal(2, query.QueryGallery(
            new LibraryFilter { RatingBucket = RatingBuckets.Ge4 }).Count);
        Assert.Equal(five, query.QueryGallery(
            new LibraryFilter { RatingBucket = RatingBuckets.Eq5 }).Single().Id);

        // AND 叠加：≥4★ 且 gray 色族。
        SetAttributes(library, four, colorFamily: "gray");
        SetAttributes(library, five, colorFamily: "red");
        var combined = query.QueryGallery(new LibraryFilter
        {
            RatingBucket = RatingBuckets.Ge4,
            ColorFamily = "gray",
        });
        Assert.Single(combined);
        Assert.Equal(four, combined[0].Id);
    }

    [Fact]
    public void Unknown_bucket_keys_are_ignored()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        library.InsertImage();

        Assert.Single(query.QueryGallery(new LibraryFilter { ResolutionBucket = "bogus" }));
        Assert.Single(query.QueryGallery(new LibraryFilter { FileSizeBucket = "bogus" }));
        Assert.Single(query.QueryGallery(new LibraryFilter { RatingBucket = "bogus" }));
    }

    // ─── M12-E: 标签组 / 文件格式 / 色板筛选 ───────────────────────────────

    [Fact]
    public void Tag_group_filter_matches_any_tag_within_group()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);
        long anime = library.InsertImage(filePath: @"X:\a.png");
        long pixel = library.InsertImage(filePath: @"X:\p.png");
        long ink = library.InsertImage(filePath: @"X:\i.png");
        long none = library.InsertImage(filePath: @"X:\n.png");
        tags.AddTags([anime], ["日系二次元"], TagSource.Manual);
        tags.AddTags([pixel], ["像素风"], TagSource.Manual);
        tags.AddTags([ink], ["水墨国风"], TagSource.Manual);

        // 组内 OR：命中任一风格即通过。
        var result = query.QueryGallery(new LibraryFilter
        {
            ArtStyleTags = ["日系二次元", "像素风"],
        });
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == anime);
        Assert.Contains(result, r => r.Id == pixel);
        Assert.DoesNotContain(result, r => r.Id == ink);
        Assert.DoesNotContain(result, r => r.Id == none);

        // null / 空列表 = 不筛选。
        Assert.Equal(4, query.QueryGallery(new LibraryFilter { ArtStyleTags = null }).Count);
        Assert.Equal(4, query.QueryGallery(new LibraryFilter { ArtStyleTags = [] }).Count);
    }

    [Fact]
    public void Tag_groups_combine_with_and_across_groups()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);
        long both = library.InsertImage(filePath: @"X:\both.png");
        long styleOnly = library.InsertImage(filePath: @"X:\style.png");
        long compOnly = library.InsertImage(filePath: @"X:\comp.png");
        tags.AddTags([both], ["厚涂", "特写"], TagSource.Manual);
        tags.AddTags([styleOnly], ["厚涂"], TagSource.Manual);
        tags.AddTags([compOnly], ["特写"], TagSource.Manual);

        // 组间 AND：美术风格与构图都命中才通过。
        var result = query.QueryGallery(new LibraryFilter
        {
            ArtStyleTags = ["厚涂"],
            CompositionTags = ["特写"],
        });
        Assert.Single(result);
        Assert.Equal(both, result[0].Id);
    }

    [Fact]
    public void File_format_filter_is_case_insensitive()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long png = library.InsertImage(filePath: @"X:\a.png");   // InsertImage 默认 format='png'
        long jpg = library.InsertImage(filePath: @"X:\b.png");
        library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE images SET format = 'JPG' WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", jpg);
            cmd.ExecuteNonQuery();
        });

        // 大小写不敏感：'PNG'/'jpg' 混排都能命中。
        var result = query.QueryGallery(new LibraryFilter
        {
            FileFormats = ["PNG", "jpg"],
        });
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == png);
        Assert.Contains(result, r => r.Id == jpg);

        Assert.Empty(query.QueryGallery(new LibraryFilter { FileFormats = ["webp"] }));
    }

    [Fact]
    public void Color_palette_hexes_filter_maps_to_families()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        long red = library.InsertImage(filePath: @"X:\red.png");
        long blue = library.InsertImage(filePath: @"X:\blue.png");
        long gray = library.InsertImage(filePath: @"X:\gray.png");
        SetAttributes(library, red, colorFamily: "red");
        SetAttributes(library, blue, colorFamily: "blue");
        SetAttributes(library, gray, colorFamily: "gray");

        // 两个 hex（红 + 蓝）→ OR 命中两张。
        var result = query.QueryGallery(new LibraryFilter
        {
            ColorPaletteHexes = ["#FF0000", "#0000FF"],
        });
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == red);
        Assert.Contains(result, r => r.Id == blue);

        // 灰色 hex（低饱和）→ gray 色族。
        Assert.Equal(gray, query.QueryGallery(
            new LibraryFilter { ColorPaletteHexes = ["#808080"] }).Single().Id);

        // 全部 hex 无法解析 → 忽略筛选（不报错、不缩小结果）。
        Assert.Equal(3, query.QueryGallery(
            new LibraryFilter { ColorPaletteHexes = ["bogus", "###"] }).Count);
    }

    [Fact]
    public void Clone_copies_new_m12e_filter_fields()
    {
        var filter = new LibraryFilter
        {
            ArtStyleTags = ["写实"],
            CompositionTags = ["特写", "全身"],
            LightingTags = ["夜景"],
            ThemeTags = ["科幻"],
            ConditionTags = ["破损"],
            FileFormats = ["png"],
            ColorPaletteHexes = ["#FF0000"],
        };

        LibraryFilter clone = filter.Clone();

        Assert.Same(filter.ArtStyleTags, clone.ArtStyleTags);
        Assert.Same(filter.CompositionTags, clone.CompositionTags);
        Assert.Same(filter.LightingTags, clone.LightingTags);
        Assert.Same(filter.ThemeTags, clone.ThemeTags);
        Assert.Same(filter.ConditionTags, clone.ConditionTags);
        Assert.Same(filter.FileFormats, clone.FileFormats);
        Assert.Same(filter.ColorPaletteHexes, clone.ColorPaletteHexes);
    }

    private static void SetAttributes(
        TestLibrary library, long id,
        int? width = null, int? height = null,
        long? fileSize = null, string? colorFamily = null)
    {
        library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE images
                SET width = COALESCE($w, width),
                    height = COALESCE($h, height),
                    file_size = COALESCE($s, file_size),
                    color_family = COALESCE($cf, color_family)
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$w", width is int w ? w : DBNull.Value);
            cmd.Parameters.AddWithValue("$h", height is int h ? h : DBNull.Value);
            cmd.Parameters.AddWithValue("$s", fileSize is long s ? s : DBNull.Value);
            cmd.Parameters.AddWithValue("$cf", colorFamily is not null ? colorFamily : DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }
}
