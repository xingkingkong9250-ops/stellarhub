using QL.World.Models;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>M6-D: SharedLibraryService tests against in-memory NAS.</summary>
public sealed class SharedLibraryServiceTests
{
    private static SharedLibraryService CreateSut()
    {
        var connector = new InMemoryNasConnector();
        var storage = new SharedStorageService(connector);
        return new SharedLibraryService(storage);
    }

    private static SharedImageRecord MakeRecord(string id, string name = "img.png")
        => new()
        {
            Id = id,
            FileName = name,
            FileHash = id,
            FileSize = 1024,
            SubmittedBy = "tester",
            ApprovedBy = "admin",
            ApprovedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task Load_returns_empty_when_no_library()
    {
        var svc = CreateSut();
        var all = await svc.LoadAsync(CancellationToken.None);
        Assert.Empty(all);
    }

    [Fact]
    public async Task AddThenLoad_returns_record()
    {
        var svc = CreateSut();
        var rec = MakeRecord("hash1");

        await svc.AddOrUpdateAsync(rec, CancellationToken.None);
        var all = await svc.LoadAsync(CancellationToken.None);

        Assert.Single(all);
        Assert.Equal("hash1", all[0].Id);
    }

    [Fact]
    public async Task AddOrUpdate_replaces_existing_by_id()
    {
        var svc = CreateSut();
        await svc.AddOrUpdateAsync(MakeRecord("hash1", "old.png"), CancellationToken.None);
        await svc.AddOrUpdateAsync(MakeRecord("hash1", "new.png"), CancellationToken.None);

        var all = await svc.LoadAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("new.png", all[0].FileName);
    }

    [Fact]
    public async Task Remove_deletes_record()
    {
        var svc = CreateSut();
        await svc.AddOrUpdateAsync(MakeRecord("hash1"), CancellationToken.None);
        await svc.AddOrUpdateAsync(MakeRecord("hash2"), CancellationToken.None);

        await svc.RemoveAsync("hash1", CancellationToken.None);

        var all = await svc.LoadAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("hash2", all[0].Id);
    }

    [Fact]
    public async Task Contains_returns_true_for_existing()
    {
        var svc = CreateSut();
        await svc.AddOrUpdateAsync(MakeRecord("hash1"), CancellationToken.None);

        Assert.True(await svc.ContainsAsync("hash1", CancellationToken.None));
        Assert.False(await svc.ContainsAsync("nope", CancellationToken.None));
    }
}
