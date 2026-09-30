using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>U6-A: shared-library gallery view model mapping and filtering.</summary>
public sealed class SharedGalleryViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly InMemoryNasConnector _nas = new();
    private readonly SharedThumbnailCache _cache;
    private readonly SharedGalleryViewModel _vm;

    public SharedGalleryViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var settings = new AppSettingsService(_dir);
        _cache = new SharedThumbnailCache(_nas, Path.Combine(_dir, "thumbs"));
        _vm = new SharedGalleryViewModel(settings, _cache);
    }

    [Fact]
    public void Load_maps_records_to_gallery_items()
    {
        _vm.Load(SampleRecords(2));

        Assert.Equal(2, _vm.Items.Count);
        Assert.Equal("a.webp", _vm.Items[0].FileName);
        Assert.Equal("hash-1", _vm.Items[0].FileHash);
        Assert.Equal("共享", _vm.Items[0].SourceBadge);
        Assert.Equal(2, _vm.Items[1].Id);
    }

    [Fact]
    public void Load_raises_refreshed_event()
    {
        int raised = 0;
        _vm.Refreshed += () => raised++;

        _vm.Load(SampleRecords(1));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Toggling_grid_raises_list_changed()
    {
        bool startedList = _vm.IsListView;

        _vm.IsGridView = !_vm.IsGridView;

        Assert.NotEqual(startedList, _vm.IsListView);
        Assert.NotEqual(_vm.IsGridView, _vm.IsListView);
    }

    [Fact]
    public void Zoom_metrics_delegate_to_settings()
    {
        _vm.GridZoom = 210;
        _vm.ListZoom = 120;

        Assert.Equal(226, _vm.GridCardWidth);
        Assert.Equal(136, _vm.ListRowHeight);
    }

    [Fact]
    public void ApplyFilter_filters_by_file_name_and_restores_all()
    {
        _vm.Load(SampleRecords(3));

        _vm.ApplyFilter("b.webp");
        Assert.Single(_vm.Items);
        Assert.Equal("b.webp", _vm.Items[0].FileName);

        _vm.ApplyFilter("猫");
        Assert.Equal(3, _vm.Items.Count);

        _vm.ApplyFilter("   ");
        Assert.Equal(3, _vm.Items.Count);
    }

    private static IReadOnlyList<SharedImageRecord> SampleRecords(int count)
    {
        var list = new List<SharedImageRecord>();
        for (int i = 1; i <= count; i++)
        {
            list.Add(new SharedImageRecord
            {
                Id = $"hash-{i}",
                FileName = $"{(char)('a' + (i - 1))}.webp",
                FileSize = 100 + i,
                Width = 8,
                Height = 8,
                Format = "webp",
                Rating = i,
                Tags = new List<string> { "猫" },
                ApprovedAt = DateTimeOffset.UtcNow,
            });
        }
        return list;
    }

    public void Dispose()
    {
        _nas.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
