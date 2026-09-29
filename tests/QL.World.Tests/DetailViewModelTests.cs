using System.IO;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;
using QL.World.ViewModels.Support;

namespace QL.World.Tests;

/// <summary>U4-A (B3-4 主色调)：DetailViewModel 对 dominant_colors 的解析、容错与选中切换刷新。</summary>
public sealed class DetailViewModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly GalleryViewModel _gallery;
    private readonly DetailViewModel _detail;

    public DetailViewModelTests()
    {
        var settings = new AppSettingsService(_lib.DirectoryRoot);
        settings.Load();
        var thumbnails = new ThumbnailService(_lib.DirectoryRoot, settings);
        var query = new ImageQueryService(_lib.Db);
        _gallery = new GalleryViewModel(query, settings, new GalleryThumbnailProvider(thumbnails));
        _detail = new DetailViewModel(
            query,
            new TagService(_lib.Db),
            _gallery,
            new SemanticSearchService(_lib.Db, new FakeAiService()));
    }

    [Fact]
    public async Task Valid_json_produces_swatch_list()
    {
        long id = Insert("colored.png");
        SetColors(id, "[\"#FF0000\",\"#00FF00\",\"#0000FF\"]");

        await SelectAndWaitAsync(id, () => _detail.DominantColors.Count == 3);

        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF" }, _detail.DominantColors);
        Assert.True(_detail.HasDominantColors);
    }

    [Fact]
    public async Task Null_or_corrupt_json_yields_empty_list_and_hides_row()
    {
        long nullId = Insert("null_colors.png"); // dominant_colors 保持 NULL
        long corruptId = Insert("corrupt.png");
        SetColors(corruptId, "not-json{{");

        await SelectAndWaitAsync(nullId, () => _detail.FileName == "null_colors.png");
        Assert.Empty(_detail.DominantColors);
        Assert.False(_detail.HasDominantColors);

        await SelectAndWaitAsync(corruptId, () => _detail.FileName == "corrupt.png");
        Assert.Empty(_detail.DominantColors);
        Assert.False(_detail.HasDominantColors);
    }

    [Fact]
    public async Task Switching_selection_refreshes_colors()
    {
        long colored = Insert("has_colors.png");
        SetColors(colored, "[\"#112233\"]");
        long plain = Insert("plain.png");

        await SelectAndWaitAsync(colored, () => _detail.FileName == "has_colors.png");
        Assert.Single(_detail.DominantColors);
        Assert.True(_detail.HasDominantColors);

        await SelectAndWaitAsync(plain, () => _detail.FileName == "plain.png");
        Assert.Empty(_detail.DominantColors);
        Assert.False(_detail.HasDominantColors);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private long Insert(string fileName) =>
        _lib.InsertImage(filePath: Path.Combine(_lib.DirectoryRoot, fileName));

    private void SetColors(long id, string json) =>
        _lib.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE images SET dominant_colors = $json WHERE id = $id;";
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>选中指定图片并轮询等待 fire-and-forget 的 ReloadSingleAsync 完成。</summary>
    private async Task SelectAndWaitAsync(long id, Func<bool> loaded)
    {
        await _gallery.RefreshAsync();
        GalleryItemViewModel item = _gallery.Items.First(i => i.Id == id);
        _gallery.SelectedItems.Clear();
        _gallery.SelectedItems.Add(item);
        _gallery.NotifySelectionChanged();

        for (int i = 0; i < 200 && !loaded(); i++)
            await Task.Delay(10);
        Assert.True(loaded(), "详情记录加载超时");
    }

    public void Dispose() => _lib.Dispose();
}
