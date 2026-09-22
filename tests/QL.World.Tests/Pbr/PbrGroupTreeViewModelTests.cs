using System.IO;
using QL.World.Services;
using QL.World.Services.Pbr;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

public sealed class PbrGroupTreeViewModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly GalleryViewModel _gallery;
    private readonly ThumbnailService _thumbnails;
    private readonly PbrGroupingService _grouping;
    private readonly PbrGroupTreeViewModel _tree;

    public PbrGroupTreeViewModelTests()
    {
        var settings = new AppSettingsService(_lib.DirectoryRoot);
        settings.Load();
        _thumbnails = new ThumbnailService(_lib.DirectoryRoot, settings);
        _gallery = new GalleryViewModel(
            new ImageQueryService(_lib.Db),
            settings,
            new GalleryThumbnailProvider(_thumbnails));
        _grouping = new PbrGroupingService(_lib.Db, settings);
        _tree = new PbrGroupTreeViewModel(_gallery, _grouping);
    }

    /// <summary>Seeds two texture sets (T_A, T_B) plus a singleton, then rescans.</summary>
    private void SeedGroups()
    {
        string dir = Path.Combine(_lib.DirectoryRoot, "tex");
        Directory.CreateDirectory(dir);
        _lib.InsertImage(Path.Combine(dir, "T_A_BaseColor.png"));
        _lib.InsertImage(Path.Combine(dir, "T_A_Normal.png"));
        _lib.InsertImage(Path.Combine(dir, "T_B_BaseColor.png"));
        _lib.InsertImage(Path.Combine(dir, "T_B_Normal.png"));
        _lib.InsertImage(Path.Combine(dir, "T_Solo.png")); // singleton → no group
        _grouping.RescanAll();
    }

    private long FirstGroupId() => _lib.Db.Run(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT id FROM pbr_groups ORDER BY name LIMIT 1;";
        return Convert.ToInt64(command.ExecuteScalar());
    });

    [Fact]
    public async Task Refresh_lists_groups_with_member_counts_ordered_by_name()
    {
        SeedGroups();

        await _tree.RefreshAsync();

        Assert.Equal(2, _tree.GroupNodes.Count);
        Assert.Equal("T_A", _tree.GroupNodes[0].DisplayName);
        Assert.Equal("T_B", _tree.GroupNodes[1].DisplayName);
        Assert.All(_tree.GroupNodes, n => Assert.Equal(2, n.MemberCount));
        Assert.All(_tree.GroupNodes, n => Assert.False(n.IsSelected));
    }

    [Fact]
    public async Task SelectNode_sets_filter_then_second_click_clears_it()
    {
        SeedGroups();
        await _tree.RefreshAsync();
        PbrGroupNodeViewModel node = _tree.GroupNodes[0];

        node.SelectCommand.Execute(null);
        Assert.Equal(node.Id, _gallery.Filter.PbrGroupId);
        Assert.True(node.IsSelected);

        node.SelectCommand.Execute(null);
        Assert.Null(_gallery.Filter.PbrGroupId);
        Assert.False(node.IsSelected);
    }

    [Fact]
    public async Task SelectNode_moves_selection_between_nodes()
    {
        SeedGroups();
        await _tree.RefreshAsync();

        _tree.GroupNodes[0].SelectCommand.Execute(null);
        Assert.Equal(_tree.GroupNodes[0].Id, _gallery.Filter.PbrGroupId);

        _tree.GroupNodes[1].SelectCommand.Execute(null);
        Assert.Equal(_tree.GroupNodes[1].Id, _gallery.Filter.PbrGroupId);
        Assert.False(_tree.GroupNodes[0].IsSelected);
        Assert.True(_tree.GroupNodes[1].IsSelected);
    }

    [Fact]
    public async Task Refresh_restores_selection_from_current_filter()
    {
        SeedGroups();
        long firstGroupId = FirstGroupId();
        _gallery.Filter.PbrGroupId = firstGroupId;

        await _tree.RefreshAsync();

        Assert.True(_tree.GroupNodes.Single(n => n.Id == firstGroupId).IsSelected);
        Assert.False(_tree.GroupNodes.Single(n => n.Id != firstGroupId).IsSelected);
    }

    [Fact]
    public async Task ResetSelection_clears_every_node_without_touching_filter()
    {
        SeedGroups();
        await _tree.RefreshAsync();
        long groupId = _tree.GroupNodes[0].Id;
        _gallery.Filter.PbrGroupId = groupId;
        await _tree.RefreshAsync();

        _tree.ResetSelection();

        Assert.All(_tree.GroupNodes, n => Assert.False(n.IsSelected));
        Assert.Equal(groupId, _gallery.Filter.PbrGroupId);
    }

    public void Dispose()
    {
        _thumbnails.Dispose();
        _lib.Dispose();
    }
}
