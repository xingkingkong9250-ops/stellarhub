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

    private static string CreateTempDir()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
