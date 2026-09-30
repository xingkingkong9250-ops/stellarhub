using QL.World.Models;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>M6-E: SubmissionService tests (submit → approve/reject flow).</summary>
public sealed class SubmissionServiceTests
{
    private static (SubmissionService svc, SharedLibraryService library) CreateSut()
    {
        var connector = new InMemoryNasConnector();
        var storage = new SharedStorageService(connector);
        var library = new SharedLibraryService(storage);
        return (new SubmissionService(storage, library), library);
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
        var (svc, _) = CreateSut();
        var sub = MakeSub();

        await svc.SubmitAsync(sub, CancellationToken.None);
        var pending = await svc.ListPendingAsync(CancellationToken.None);

        Assert.Single(pending);
        Assert.Equal(sub.SubmitId, pending[0].SubmitId);
    }

    [Fact]
    public async Task Approve_promotes_to_library_and_removes_pending()
    {
        var (svc, library) = CreateSut();
        var sub = MakeSub("hashA");
        await svc.SubmitAsync(sub, CancellationToken.None);

        var record = await svc.ApproveAsync(sub, "admin", CancellationToken.None);

        Assert.Equal("hashA", record.Id);
        Assert.True(await library.ContainsAsync("hashA", CancellationToken.None));
        var pending = await svc.ListPendingAsync(CancellationToken.None);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Reject_removes_pending_without_promoting()
    {
        var (svc, library) = CreateSut();
        var sub = MakeSub("hashB");
        await svc.SubmitAsync(sub, CancellationToken.None);

        await svc.RejectAsync(sub.SubmitId, CancellationToken.None);

        Assert.False(await library.ContainsAsync("hashB", CancellationToken.None));
        var pending = await svc.ListPendingAsync(CancellationToken.None);
        Assert.Empty(pending);
    }
}
