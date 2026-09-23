using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>M4-D server-side pagination behaviour of the gallery VM.</summary>
public sealed class GalleryViewModelPaginationTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly AppSettingsService _settings;
    private readonly ThumbnailService _thumbnails;
    private readonly GalleryViewModel _gallery;

    public GalleryViewModelPaginationTests()
    {
        _settings = new AppSettingsService(_lib.DirectoryRoot);
        _settings.Load();
        _thumbnails = new ThumbnailService(_lib.DirectoryRoot, _settings);
        _gallery = new GalleryViewModel(
            new ImageQueryService(_lib.Db),
            _settings,
            new GalleryThumbnailProvider(_thumbnails));
    }

    [Fact]
    public async Task Refresh_loads_first_page_and_flags_more()
    {
        for (int i = 0; i < 1001; i++)
            _lib.InsertImage();

        await _gallery.RefreshAsync();

        Assert.Equal(1000, _gallery.Items.Count);
        Assert.Equal(1001, _gallery.FilteredCount);
        Assert.Equal(1001, _gallery.TotalCount);
        Assert.True(_gallery.HasMoreItems);
    }

    [Fact]
    public async Task LoadMore_appends_remaining_rows_then_stops()
    {
        for (int i = 0; i < 1001; i++)
            _lib.InsertImage();
        await _gallery.RefreshAsync();

        await _gallery.LoadMoreAsync();

        Assert.Equal(1001, _gallery.Items.Count);
        Assert.False(_gallery.HasMoreItems);

        // Further requests are no-ops.
        await _gallery.LoadMoreAsync();
        Assert.Equal(1001, _gallery.Items.Count);
    }

    [Fact]
    public async Task Refresh_resets_pagination_for_new_filter()
    {
        for (int i = 0; i < 1001; i++)
            _lib.InsertImage();
        await _gallery.RefreshAsync();
        await _gallery.LoadMoreAsync();
        Assert.False(_gallery.HasMoreItems);

        _gallery.Filter.Tag = "nomatch";
        await _gallery.RefreshAsync();

        Assert.Empty(_gallery.Items);
        Assert.Equal(0, _gallery.FilteredCount);
        Assert.False(_gallery.HasMoreItems);
    }

    [Fact]
    public async Task Small_library_never_flags_more()
    {
        for (int i = 0; i < 5; i++)
            _lib.InsertImage();

        await _gallery.RefreshAsync();

        Assert.Equal(5, _gallery.Items.Count);
        Assert.False(_gallery.HasMoreItems);
        await _gallery.LoadMoreAsync();
        Assert.Equal(5, _gallery.Items.Count);
    }

    public void Dispose()
    {
        _thumbnails.Dispose();
        _lib.Dispose();
    }
}
