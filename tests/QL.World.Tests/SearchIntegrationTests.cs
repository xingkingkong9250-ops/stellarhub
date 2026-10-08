using System.IO;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// F-09 / F-10 regression: direct ImageQueryService verification that search
/// text + category filters return correct rows end-to-end.
/// </summary>
public sealed class SearchIntegrationTests : IDisposable
{
    private readonly TestLibrary _library = new();
    private readonly ImageQueryService _query;

    public SearchIntegrationTests()
    {
        _query = new ImageQueryService(_library.Db);
        var tags = new TagService(_library.Db);

        long dragon = _library.InsertImage(filePath: @"X:\dragon.png", category: Categories.ConceptDesign);
        long metal = _library.InsertImage(filePath: @"X:\metal.jpg", category: Categories.MaterialReference);
        long photo = _library.InsertImage(filePath: @"X:\photo.png", category: Categories.PhotoReference);
        _library.InsertImage(filePath: @"X:\unknown.jpg", category: null);

        tags.AddTags([dragon], ["dragon", "fantasy"], TagSource.Manual);
        tags.AddTags([metal], ["metal", "rust"], TagSource.Manual);
        tags.AddTags([photo], ["outdoor", "nature"], TagSource.Manual);
    }

    public void Dispose() => _library.Dispose();

    // ─── F-09: search ───────────────────────────────────────────────────────

    [Fact]
    public void Tag_search_finds_partial_match()
    {
        var result = _query.QueryGallery(new LibraryFilter
        {
            Text = "dra", SearchMode = SearchMode.Tag,
        });
        Assert.Single(result);
        Assert.Equal("dragon.png", result[0].FileName);
    }

    [Fact]
    public void Tag_search_exact_match()
    {
        var result = _query.QueryGallery(new LibraryFilter
        {
            Text = "metal", SearchMode = SearchMode.Tag,
        });
        Assert.Single(result);
        Assert.Equal("metal.jpg", result[0].FileName);
    }

    [Fact]
    public void FileName_search_finds_substring()
    {
        var result = _query.QueryGallery(new LibraryFilter
        {
            Text = "photo", SearchMode = SearchMode.FileNameNotes,
        });
        Assert.Single(result);
        Assert.Equal("photo.png", result[0].FileName);
    }

    [Fact]
    public void Search_empty_result_when_no_match()
    {
        var result = _query.QueryGallery(new LibraryFilter
        {
            Text = "zzzz_no_match", SearchMode = SearchMode.Tag,
        });
        Assert.Empty(result);
    }

    // ─── F-10: category switch ──────────────────────────────────────────────

    [Fact]
    public void Category_switch_shows_correct_rows()
    {
        var all = _query.QueryGallery(new LibraryFilter());
        Assert.Equal(4, all.Count);

        var concept = _query.QueryGallery(new LibraryFilter
        {
            Category = Categories.ConceptDesign,
        });
        Assert.Single(concept);
        Assert.Equal("dragon.png", concept[0].FileName);

        var material = _query.QueryGallery(new LibraryFilter
        {
            Category = Categories.MaterialReference,
        });
        Assert.Single(material);
        Assert.Equal("metal.jpg", material[0].FileName);
    }

    [Fact]
    public void Uncategorized_shows_null_category_rows()
    {
        var result = _query.QueryGallery(new LibraryFilter { Uncategorized = true });
        Assert.Single(result);
        Assert.Null(result[0].PrimaryCategory);
    }

    // ─── F-10: category + search combined ───────────────────────────────────

    [Fact]
    public void Category_and_search_combine_with_and()
    {
        var result = _query.QueryGallery(new LibraryFilter
        {
            Category = Categories.MaterialReference,
            Text = "rust",
            SearchMode = SearchMode.Tag,
        });
        Assert.Single(result);
        Assert.Equal("metal.jpg", result[0].FileName);

        // AND semantics: searching "dragon" inside MaterialReference yields 0.
        var miss = _query.QueryGallery(new LibraryFilter
        {
            Category = Categories.MaterialReference,
            Text = "dragon",
            SearchMode = SearchMode.Tag,
        });
        Assert.Empty(miss);
    }

    // ─── F-10: rapid category switch stress ─────────────────────────────────

    [Fact]
    public void Rapid_category_switch_always_yields_correct_count()
    {
        // Simulates rapid clicking through category nodes. Each switch replaces
        // the previous filter entirely. No stale results.
        string[] cats = [Categories.ConceptDesign, Categories.MaterialReference,
                         Categories.PhotoReference, Categories.ConceptDesign,
                         Categories.MaterialReference];

        foreach (string cat in cats)
        {
            var result = _query.QueryGallery(new LibraryFilter { Category = cat });
            Assert.Single(result);
        }

        // Switching back to "all" shows everything again.
        var all = _query.QueryGallery(new LibraryFilter());
        Assert.Equal(4, all.Count);
    }
}
