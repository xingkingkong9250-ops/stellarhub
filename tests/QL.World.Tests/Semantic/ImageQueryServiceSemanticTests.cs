using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests.Semantic;

/// <summary>
/// ImageQueryService.AppendClauses semantic branch: filter by pre-computed id list.
/// Tests cover both the id-list branch and the empty-list (1=0) branch.
/// </summary>
public sealed class ImageQueryServiceSemanticTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly ImageQueryService _query;

    public ImageQueryServiceSemanticTests() => _query = new ImageQueryService(_lib.Db);

    public void Dispose() => _lib.Dispose();

    [Fact]
    public void Semantic_filter_limits_to_id_list()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();
        long id3 = _lib.InsertImage(); // 不在 SemanticResultIds 内，应被排除

        var filter = new LibraryFilter
        {
            SearchMode = SearchMode.Semantic,
            SemanticResultIds = [id1, id2],
        };

        var items = _query.QueryGallery(filter);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == id1);
        Assert.Contains(items, i => i.Id == id2);
        Assert.DoesNotContain(items, i => i.Id == id3);
    }

    [Fact]
    public void Semantic_filter_empty_list_returns_zero_rows()
    {
        _lib.InsertImage();
        _lib.InsertImage();

        var filter = new LibraryFilter
        {
            SearchMode = SearchMode.Semantic,
            SemanticResultIds = Array.Empty<long>(),
        };

        var items = _query.QueryGallery(filter);

        Assert.Empty(items);
    }

    [Fact]
    public void Semantic_mode_with_null_list_also_returns_zero_rows()
    {
        _lib.InsertImage();

        // SearchMode=Semantic 但 SemanticResultIds=null：用户切换到语义档但还没输入查询。
        // 应显示空，而不是「全部图片」（避免误导）。
        var filter = new LibraryFilter
        {
            SearchMode = SearchMode.Semantic,
            SemanticResultIds = null,
        };

        var items = _query.QueryGallery(filter);

        Assert.Empty(items);
    }

    [Fact]
    public void Semantic_filter_ignores_text_and_tag_filters()
    {
        long id1 = _lib.InsertImage();
        long id2 = _lib.InsertImage();

        // 即使设了 Text="nonexistent" 和 Tag="missing"，语义结果集优先。
        var filter = new LibraryFilter
        {
            SearchMode = SearchMode.Semantic,
            SemanticResultIds = [id1, id2],
            Text = "nonexistent",
            Tag = "missing",
        };

        var items = _query.QueryGallery(filter);

        Assert.Equal(2, items.Count);
    }
}
