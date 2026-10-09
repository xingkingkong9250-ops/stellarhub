using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>
/// M11-F: W3/A1「显示未托管图」翻转 → AppSettingsService
/// LegacyVisibilityChanged → GalleryViewModel 自动按新谓词重查。
/// </summary>
public sealed class GalleryLegacyVisibilityTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly AppSettingsService _settings;
    private readonly GalleryViewModel _gallery;

    public GalleryLegacyVisibilityTests()
    {
        _settings = new AppSettingsService(_lib.DirectoryRoot);
        _settings.Load();
        var query = new ImageQueryService(
            _lib.Db,
            () => _settings.LegacyDataSkipped && !_settings.ShowLegacyImages);
        var thumbnails = new ThumbnailService(_lib.DirectoryRoot, _settings);
        _gallery = new GalleryViewModel(
            query, _settings, new GalleryThumbnailProvider(thumbnails));
    }

    public void Dispose() => _lib.Dispose();

    [Fact]
    public async Task Toggling_show_legacy_refreshes_gallery_automatically()
    {
        _lib.InsertImage(); // unmanaged legacy row
        long managedId = _lib.InsertImage();
        _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE images SET library_path = file_path WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", managedId);
            cmd.ExecuteNonQuery();
        });

        _settings.LegacyDataSkipped = true;
        await _gallery.RefreshAsync();
        Assert.Single(_gallery.Items); // unmanaged row hidden

        var refreshed = new TaskCompletionSource();
        _gallery.Refreshed += () => refreshed.TrySetResult();

        _settings.ShowLegacyImages = true;

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, _gallery.Items.Count);
    }
}
