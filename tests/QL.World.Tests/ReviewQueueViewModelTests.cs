using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>U6-C: review queue window view model (load, approve, reject, thumbnail).</summary>
public sealed class ReviewQueueViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly InMemoryNasConnector _connector;
    private readonly SharedStorageService _storage;
    private readonly SharedLibraryService _library;
    private readonly ThumbnailService _thumbnailService;
    private readonly AppSettingsService _settings;
    private readonly SubmissionService _submissions;
    private readonly SharedThumbnailCache _cache;
    private readonly ReviewQueueViewModel _sut;

    public ReviewQueueViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings = new AppSettingsService(_dir);
        _thumbnailService = new ThumbnailService(_dir, _settings);
        _connector = new InMemoryNasConnector();
        _storage = new SharedStorageService(_connector);
        _storage.EnsureSharedStructureAsync(CancellationToken.None).GetAwaiter().GetResult();
        _library = new SharedLibraryService(_storage);
        _submissions = new SubmissionService(_storage, _library, _thumbnailService, _settings);
        _cache = new SharedThumbnailCache(_connector, Path.Combine(_dir, "cache"));
        _sut = new ReviewQueueViewModel(_submissions, _cache, "admin");
    }

    [Fact]
    public async Task Load_populates_pending_rows()
    {
        await SeedPendingAsync("h1", "a.png");
        await SeedPendingAsync("h2", "b.png");

        await _sut.LoadAsync();

        Assert.Equal(2, _sut.Items.Count);
        Assert.Equal("a.png", _sut.Items[0].FileName);
        Assert.Equal("alice", _sut.Items[0].SubmittedBy);
    }

    [Fact]
    public async Task Approve_removes_row_and_promotes_record()
    {
        await SeedPendingAsync("hA", "a.png");
        await _sut.LoadAsync();
        var row = Assert.Single(_sut.Items);

        _sut.ApproveCommand.Execute(row);
        await WaitUntilAsync(() => _sut.Items.Count == 0);

        Assert.Empty(await _submissions.ListPendingAsync(CancellationToken.None));
        Assert.True(await _library.ContainsAsync("hA", CancellationToken.None));
    }

    [Fact]
    public async Task Reject_removes_row_without_promoting()
    {
        await SeedPendingAsync("hB", "b.png");
        await _sut.LoadAsync();
        var row = Assert.Single(_sut.Items);

        _sut.RejectCommand.Execute(row);
        await WaitUntilAsync(() => _sut.Items.Count == 0);

        Assert.Empty(await _submissions.ListPendingAsync(CancellationToken.None));
        Assert.False(await _library.ContainsAsync("hB", CancellationToken.None));
    }

    [Fact]
    public async Task Load_attaches_pending_thumbnail()
    {
        await SeedPendingAsync("hT", "t.png", withThumbnail: true);
        await _sut.LoadAsync();
        var row = Assert.Single(_sut.Items);

        await WaitUntilAsync(() => row.Thumbnail is not null);
        Assert.NotNull(row.Thumbnail);
    }

    private async Task SeedPendingAsync(string hash, string fileName, bool withThumbnail = false)
    {
        string submitId = Guid.NewGuid().ToString("N");
        var sub = new PendingSubmission
        {
            SubmitId = submitId,
            FileName = fileName,
            FileHash = hash,
            FileSize = 512,
            Width = 8,
            Height = 8,
            Format = "png",
            SubmittedBy = "alice",
            OriginalNasPath = SharedPaths.PendingOriginal(submitId, fileName),
            ThumbnailNasPath = withThumbnail ? SharedPaths.PendingThumbnail(submitId) : null,
        };
        await _storage.SavePendingAsync(sub, CancellationToken.None);
        _connector.MakeDirectoryAsync(SharedPaths.PendingFiles(submitId), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (withThumbnail)
            _connector.SeedBytes(SharedPaths.PendingThumbnail(submitId), EncodeWebp());
    }

    private static byte[] EncodeWebp()
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(50, 90, 200));
        using var ms = new MemoryStream();
        image.Save(ms, new WebpEncoder());
        return ms.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("condition was not met before timeout");
    }

    public void Dispose()
    {
        _thumbnailService.Dispose();
        _connector.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
