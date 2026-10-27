using QL.World.Services.Nas;
using QL.World.Tests.Support;
using Xunit;

namespace QL.World.Tests;

/// <summary>
/// PEND-004 (0.18.0): shared collections persist in collections.json through
/// an in-memory INasConnector. Covers create/duplicate/add/remove/delete.
/// </summary>
public class SharedCollectionServiceTests
{
    private readonly InMemoryNasConnector _nas = new();
    private readonly SharedCollectionService _service;

    public SharedCollectionServiceTests()
    {
        _service = new SharedCollectionService(new SharedStorageService(_nas));
    }

    [Fact]
    public async Task Create_persists_and_loads_back()
    {
        var created = await _service.CreateAsync(" Brick Walls ", "alice", CancellationToken.None);

        var all = await _service.LoadAsync(CancellationToken.None);

        Assert.Single(all);
        Assert.Equal("Brick Walls", all[0].Name);
        Assert.Equal("alice", all[0].CreatedBy);
        Assert.Equal(created.Id, all[0].Id);
        Assert.False(string.IsNullOrEmpty(created.Id));
    }

    [Fact]
    public async Task Create_rejects_duplicate_name_case_insensitively()
    {
        await _service.CreateAsync("Brick Walls", "alice", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateAsync("brick walls", "bob", CancellationToken.None));
    }

    [Fact]
    public async Task AddImages_is_idempotent_and_keeps_order()
    {
        var created = await _service.CreateAsync("Refs", "alice", CancellationToken.None);

        int first = await _service.AddImagesAsync(created.Id, ["h1", "h2"], CancellationToken.None);
        int second = await _service.AddImagesAsync(created.Id, ["h2", "h3"], CancellationToken.None);

        Assert.Equal(2, first);
        Assert.Equal(1, second);
        var all = await _service.LoadAsync(CancellationToken.None);
        Assert.Equal(new[] { "h1", "h2", "h3" }, all[0].ImageIds);
    }

    [Fact]
    public async Task RemoveImages_drops_membership_only()
    {
        var created = await _service.CreateAsync("Refs", "alice", CancellationToken.None);
        await _service.AddImagesAsync(created.Id, ["h1", "h2"], CancellationToken.None);

        int removed = await _service.RemoveImagesAsync(created.Id, ["h1", "missing"], CancellationToken.None);

        Assert.Equal(1, removed);
        var all = await _service.LoadAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal(new[] { "h2" }, all[0].ImageIds);
    }

    [Fact]
    public async Task Delete_removes_collection()
    {
        var created = await _service.CreateAsync("Refs", "alice", CancellationToken.None);

        await _service.DeleteAsync(created.Id, CancellationToken.None);

        Assert.Empty(await _service.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddImages_to_missing_collection_is_noop()
    {
        int added = await _service.AddImagesAsync("nope", ["h1"], CancellationToken.None);

        Assert.Equal(0, added);
        Assert.Empty(await _service.LoadAsync(CancellationToken.None));
    }
}
