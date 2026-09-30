using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>M6-E / U6-B: SubmissionService tests (submit → approve/reject, local submit).</summary>
public sealed class SubmissionServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly InMemoryNasConnector _connector;
    private readonly SharedStorageService _storage;
    private readonly SharedLibraryService _library;
    private readonly ThumbnailService _thumbs;
    private readonly AppSettingsService _settings;
    private readonly SubmissionService _svc;

    public SubmissionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings = new AppSettingsService(_dir);
        _thumbs = new ThumbnailService(_dir, _settings);
        _connector = new InMemoryNasConnector();
        _storage = new SharedStorageService(_connector);
        _storage.EnsureSharedStructureAsync(CancellationToken.None).GetAwaiter().GetResult();
        _library = new SharedLibraryService(_storage);
        _svc = new SubmissionService(_storage, _library, _thumbs, _settings);
    }

    private static PendingSubmission MakeSub(string hash = "hash1", string name = "img.png")
        => new()
        {
            SubmitId = Guid.NewGuid().ToString("N"),
            FileName = name,
            FileHash = hash,
            FileSize = 2048,
            SubmittedBy = "alice",
        };

    [Fact]
    public async Task Submit_then_ListPending_returns_submission()
    {
        var sub = MakeSub();

        await _svc.SubmitAsync(sub, CancellationToken.None);
        var pending = await _svc.ListPendingAsync(CancellationToken.None);

        Assert.Single(pending);
        Assert.Equal(sub.SubmitId, pending[0].SubmitId);
    }

    [Fact]
    public async Task Approve_promotes_to_library_and_removes_pending()
    {
        var sub = MakeSub("hashA");
        await _svc.SubmitAsync(sub, CancellationToken.None);

        var record = await _svc.ApproveAsync(sub, "admin", CancellationToken.None);

        Assert.Equal("hashA", record.Id);
        Assert.True(await _library.ContainsAsync("hashA", CancellationToken.None));
        var pending = await _svc.ListPendingAsync(CancellationToken.None);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Reject_removes_pending_without_promoting()
    {
        var sub = MakeSub("hashB");
        await _svc.SubmitAsync(sub, CancellationToken.None);

        await _svc.RejectAsync(sub.SubmitId, CancellationToken.None);

        Assert.False(await _library.ContainsAsync("hashB", CancellationToken.None));
        var pending = await _svc.ListPendingAsync(CancellationToken.None);
        Assert.Empty(pending);
    }

    // ─── U6-B: SubmitFromLocalAsync ─────────────────────────────────────

    [Fact]
    public async Task SubmitFromLocal_uploads_original_and_thumbnail_then_records_json()
    {
        var item = MakeLocalItem("cat.png", "h1");

        var result = await _svc.SubmitFromLocalAsync(item, "alice", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.SubmitId);

        var pending = await _svc.ListPendingAsync(CancellationToken.None);
        var sub = Assert.Single(pending);
        Assert.Equal("h1", sub.FileHash);
        Assert.Equal("alice", sub.SubmittedBy);
        Assert.Equal(SharedPaths.PendingOriginal(result.SubmitId, "cat.png"),
            sub.OriginalNasPath);
        Assert.Equal(SharedPaths.PendingThumbnail(result.SubmitId),
            sub.ThumbnailNasPath);
        Assert.Equal(new[] { "猫", "宠物" }, sub.Tags);

        // Original and thumbnail bytes actually reachable on the NAS.
        using (await _connector.OpenReadAsync(sub.OriginalNasPath!, CancellationToken.None)) { }
        using (await _connector.OpenReadAsync(sub.ThumbnailNasPath!, CancellationToken.None)) { }

        // Temp encoded files are cleaned up after upload.
        string tempDir = Path.Combine(_dir, "temp");
        Assert.True(!Directory.Exists(tempDir) || Directory.GetFiles(tempDir).Length == 0);
    }

    [Fact]
    public async Task SubmitFromLocal_returns_DuplicateInLibrary_when_hash_exists()
    {
        await _library.AddOrUpdateAsync(
            new SharedImageRecord { Id = "h1", FileHash = "h1", FileName = "cat.png" },
            CancellationToken.None);

        var result = await _svc.SubmitFromLocalAsync(MakeLocalItem("cat.png", "h1"),
            "alice", CancellationToken.None);

        Assert.Equal(SubmissionOutcome.DuplicateInLibrary, result.Outcome);
        Assert.Empty(await _svc.ListPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SubmitFromLocal_returns_DuplicatePending_when_awaiting_review()
    {
        await _storage.SavePendingAsync(
            new PendingSubmission { SubmitId = "earlier", FileHash = "h1" },
            CancellationToken.None);

        var result = await _svc.SubmitFromLocalAsync(MakeLocalItem("cat.png", "h1"),
            "alice", CancellationToken.None);

        Assert.Equal(SubmissionOutcome.DuplicatePending, result.Outcome);
        Assert.Single(await _svc.ListPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Approve_relocates_pending_files_into_library_shards()
    {
        var item = MakeLocalItem("cat.png", "hF");
        await _svc.SubmitFromLocalAsync(item, "alice", CancellationToken.None);
        var sub = Assert.Single(await _svc.ListPendingAsync(CancellationToken.None));

        var record = await _svc.ApproveAsync(sub, "admin", CancellationToken.None);

        Assert.Equal(SharedPaths.Original("hF", ".png"), record.SourceNasPath);
        using (await _connector.OpenReadAsync(record.SourceNasPath!, CancellationToken.None)) { }
        using (await _connector.OpenReadAsync(SharedPaths.Thumbnail("hF"), CancellationToken.None)) { }
        await Assert.ThrowsAsync<NasConnectionException>(() => _connector.OpenReadAsync(
            SharedPaths.PendingOriginal(sub.SubmitId, "cat.png"), CancellationToken.None));
    }

    [Fact]
    public async Task SubmitFromLocal_returns_MissingLocalFile_for_bad_path()
    {
        var item = new GalleryItemViewModel(new GalleryListItem
        {
            Id = 1,
            FileName = "ghost.png",
            FilePath = Path.Combine(_dir, "does-not-exist.png"),
            FileHash = "h9",
        });

        var result = await _svc.SubmitFromLocalAsync(item, "alice", CancellationToken.None);

        Assert.Equal(SubmissionOutcome.MissingLocalFile, result.Outcome);
    }

    private GalleryItemViewModel MakeLocalItem(string fileName, string hash)
    {
        string path = Path.Combine(_dir, fileName);
        using (var image = new Image<Rgba32>(12, 10, new Rgba32(10, 200, 10)))
            image.SaveAsPng(path);

        return new GalleryItemViewModel(new GalleryListItem
        {
            Id = 1,
            FileName = fileName,
            FilePath = path,
            FileHash = hash,
            FileSize = new FileInfo(path).Length,
            Width = 12,
            Height = 10,
            Format = "png",
            TagsCsv = "猫,宠物",
        });
    }

    public void Dispose()
    {
        _thumbs.Dispose();
        _connector.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
