using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>U6-D: read-only shared detail pane — selection fields and save-copy.</summary>
public sealed class SharedDetailViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly InMemoryNasConnector _nas = new();
    private readonly SharedGalleryViewModel _gallery;
    private readonly SharedDetailViewModel _detail;

    public SharedDetailViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var settings = new AppSettingsService(_dir);
        var cache = new SharedThumbnailCache(_nas, Path.Combine(_dir, "thumbs"));
        _gallery = new SharedGalleryViewModel(settings, cache);
        _detail = new SharedDetailViewModel(_gallery, _nas);
    }

    [Fact]
    public void Starts_empty()
    {
        Assert.True(_detail.ShowEmpty);
        Assert.False(_detail.HasSelection);
        Assert.Null(_detail.NasPath);
    }

    [Fact]
    public void Selection_populates_readonly_fields()
    {
        _gallery.Load([SampleRecord()]);
        SelectFirst();

        Assert.True(_detail.HasSelection);
        Assert.False(_detail.ShowEmpty);
        Assert.Equal("cat.png", _detail.FileName);
        Assert.Equal("alice", _detail.SubmittedBy);
        Assert.Equal("admin", _detail.ApprovedBy);
        Assert.Equal(4, _detail.Rating);
        Assert.Equal("originals/hF/cat.png", _detail.NasPath);
        Assert.Equal("猫,风景", _detail.TagsSummary);
    }

    [Fact]
    public async Task SaveCopy_downloads_original_and_reports_success()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        _nas.SeedBytes("originals/hF/cat.png", payload);
        _gallery.Load([SampleRecord()]);
        SelectFirst();

        string target = Path.Combine(_dir, "saved-copy.png");
        _detail.RequestSaveFile += _ => target;

        var completions = new List<(NotificationKind, string, string)>();
        _detail.SaveCompleted += (kind, title, msg) => completions.Add((kind, title, msg));

        await _detail.SaveCopyCommand.ExecuteAsync();

        Assert.True(File.Exists(target));
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Single(completions);
        Assert.Equal("已保存副本", completions[0].Item2);
    }

    [Fact]
    public async Task SaveCopy_cancel_does_nothing()
    {
        _nas.SeedBytes("originals/hF/cat.png", new byte[] { 9 });
        _gallery.Load([SampleRecord()]);
        SelectFirst();
        _detail.RequestSaveFile += _ => null;

        int raised = 0;
        _detail.SaveCompleted += (_, _, _) => raised++;

        await _detail.SaveCopyCommand.ExecuteAsync();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task SaveCopy_missing_original_reports_failure()
    {
        _gallery.Load([SampleRecord()]);
        SelectFirst();
        string target = Path.Combine(_dir, "never.png");
        _detail.RequestSaveFile += _ => target;

        var completions = new List<(NotificationKind, string, string)>();
        _detail.SaveCompleted += (kind, title, msg) => completions.Add((kind, title, msg));

        await _detail.SaveCopyCommand.ExecuteAsync();

        Assert.False(File.Exists(target));
        Assert.Single(completions);
        Assert.Equal("保存副本失败", completions[0].Item2);
    }

    private void SelectFirst()
    {
        _gallery.SelectedItems.Add(_gallery.Items[0]);
        _gallery.NotifySelectionChanged();
    }

    private static SharedImageRecord SampleRecord() => new()
    {
        Id = "hF",
        FileName = "cat.png",
        FileSize = 4096,
        Width = 1024,
        Height = 768,
        Format = "png",
        Rating = 4,
        Tags = ["猫", "风景"],
        SourceNasPath = "originals/hF/cat.png",
        SubmittedBy = "alice",
        ApprovedBy = "admin",
        ApprovedAt = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        _nas.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
