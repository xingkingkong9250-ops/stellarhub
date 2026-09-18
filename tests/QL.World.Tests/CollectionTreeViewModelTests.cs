using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

public sealed class CollectionTreeViewModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly GalleryViewModel _gallery;
    private readonly ThumbnailService _thumbnails;
    private readonly CollectionTreeViewModel _tree;

    public CollectionTreeViewModelTests()
    {
        var settings = new AppSettingsService(_lib.DirectoryRoot);
        settings.Load();
        _thumbnails = new ThumbnailService(_lib.DirectoryRoot, settings);
        _gallery = new GalleryViewModel(
            new ImageQueryService(_lib.Db),
            settings,
            new GalleryThumbnailProvider(_thumbnails));
        _tree = new CollectionTreeViewModel(_gallery, new CollectionService(_lib.Db));
    }

    [Fact]
    public async Task Refresh_builds_nodes_ordered_by_name()
    {
        var collections = new CollectionService(_lib.Db);
        collections.Create("Beta");
        collections.Create("Alpha");

        await _tree.RefreshAsync();

        Assert.Equal(2, _tree.CollectionNodes.Count);
        Assert.Equal("Alpha", _tree.CollectionNodes[0].DisplayName);
        Assert.Equal("Beta", _tree.CollectionNodes[1].DisplayName);
        Assert.All(_tree.CollectionNodes, n => Assert.False(n.IsSelected));
    }

    [Fact]
    public async Task Refresh_reports_image_count_per_node()
    {
        var collections = new CollectionService(_lib.Db);
        long colId = collections.Create("Counted");
        long imgId = _lib.InsertImage();
        collections.AddImages(colId, [imgId]);

        await _tree.RefreshAsync();

        var node = Assert.Single(_tree.CollectionNodes);
        Assert.Equal(1, node.Count);
    }

    [Fact]
    public async Task SelectNode_sets_filter_then_second_click_clears_it()
    {
        var collections = new CollectionService(_lib.Db);
        collections.Create("Pick Me");
        await _tree.RefreshAsync();
        CollectionNodeViewModel node = _tree.CollectionNodes[0];

        node.SelectCommand.Execute(null);
        Assert.Equal(node.Id, _gallery.Filter.CollectionId);
        Assert.True(node.IsSelected);

        node.SelectCommand.Execute(null);
        Assert.Null(_gallery.Filter.CollectionId);
        Assert.False(node.IsSelected);
    }

    [Fact]
    public async Task SelectNode_moves_selection_between_nodes()
    {
        var collections = new CollectionService(_lib.Db);
        collections.Create("First");
        collections.Create("Second");
        await _tree.RefreshAsync();

        _tree.CollectionNodes[0].SelectCommand.Execute(null);
        Assert.Equal(_tree.CollectionNodes[0].Id, _gallery.Filter.CollectionId);

        _tree.CollectionNodes[1].SelectCommand.Execute(null);
        Assert.Equal(_tree.CollectionNodes[1].Id, _gallery.Filter.CollectionId);
        Assert.False(_tree.CollectionNodes[0].IsSelected);
        Assert.True(_tree.CollectionNodes[1].IsSelected);
    }

    [Fact]
    public async Task Refresh_restores_selection_from_current_filter()
    {
        var collections = new CollectionService(_lib.Db);
        long colId = collections.Create("Active");
        collections.Create("Idle");
        _gallery.Filter.CollectionId = colId;

        await _tree.RefreshAsync();

        Assert.True(_tree.CollectionNodes.Single(n => n.Id == colId).IsSelected);
        Assert.False(_tree.CollectionNodes.Single(n => n.Id != colId).IsSelected);
    }

    [Fact]
    public async Task ResetSelection_clears_every_node_without_touching_filter()
    {
        var collections = new CollectionService(_lib.Db);
        long colId = collections.Create("Keep Filter");
        _gallery.Filter.CollectionId = colId;
        await _tree.RefreshAsync();

        _tree.ResetSelection();

        Assert.All(_tree.CollectionNodes, n => Assert.False(n.IsSelected));
        Assert.Equal(colId, _gallery.Filter.CollectionId);
    }

    [Fact]
    public async Task Refresh_clears_stale_selection_for_deleted_collection()
    {
        var collections = new CollectionService(_lib.Db);
        long colId = collections.Create("Doomed");
        _gallery.Filter.CollectionId = colId;
        await _tree.RefreshAsync();

        collections.Delete(colId);
        await _tree.RefreshAsync();

        Assert.Empty(_tree.CollectionNodes);
    }

    public void Dispose()
    {
        _thumbnails.Dispose();
        _lib.Dispose();
    }
}
