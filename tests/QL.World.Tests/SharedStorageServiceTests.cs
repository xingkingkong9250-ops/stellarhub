using System.Text.Json;
using QL.World.Models;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using Xunit;

namespace QL.World.Tests;

/// <summary>
/// M6-A: SharedStorageService reads/writes JSON files through an
/// in-memory INasConnector. Covers accounts/library/pending round-trip,
/// missing-file returns empty, pending listing filters .json only.
/// </summary>
public class SharedStorageServiceTests
{
    private readonly InMemoryNasConnector _nas = new();
    private readonly SharedStorageService _storage;

    public SharedStorageServiceTests()
    {
        _storage = new SharedStorageService(_nas);
    }

    [Fact]
    public async Task LoadAccounts_returns_empty_when_file_missing()
    {
        var accounts = await _storage.LoadAccountsAsync(CancellationToken.None);
        Assert.Empty(accounts);
    }

    [Fact]
    public async Task Save_then_Load_accounts_round_trips()
    {
        var accounts = new List<UserAccount>
        {
            new() { Username = "alice", Role = UserRole.User, Status = "active" },
        };

        await _storage.SaveAccountsAsync(accounts, CancellationToken.None);
        var loaded = await _storage.LoadAccountsAsync(CancellationToken.None);

        Assert.Single(loaded);
        Assert.Equal("alice", loaded[0].Username);
    }

    [Fact]
    public async Task SaveLibrary_then_Load_round_trips()
    {
        var images = new List<SharedImageRecord>
        {
            new() { Id = "abc123", FileName = "brick.png", FileHash = "abc123" },
        };

        await _storage.SaveLibraryAsync(images, CancellationToken.None);
        var loaded = await _storage.LoadLibraryAsync(CancellationToken.None);

        Assert.Single(loaded);
        Assert.Equal("brick.png", loaded[0].FileName);
    }

    [Fact]
    public async Task SaveCollections_then_Load_round_trips()
    {
        var collections = new List<SharedCollection>
        {
            new() { Id = "c1", Name = "Brick", ImageIds = ["h1"], CreatedBy = "alice" },
        };

        await _storage.SaveCollectionsAsync(collections, CancellationToken.None);
        var loaded = await _storage.LoadCollectionsAsync(CancellationToken.None);

        Assert.Single(loaded);
        Assert.Equal("Brick", loaded[0].Name);
        Assert.Equal(new[] { "h1" }, loaded[0].ImageIds);
    }

    [Fact]
    public async Task LoadCollections_returns_empty_when_missing()
    {
        Assert.Empty(await _storage.LoadCollectionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadLibrary_returns_empty_when_missing()
    {
        var images = await _storage.LoadLibraryAsync(CancellationToken.None);
        Assert.Empty(images);
    }

    [Fact]
    public async Task SavePending_then_LoadPending_round_trips()
    {
        var sub = new PendingSubmission
        {
            SubmitId = "sub-001",
            FileName = "brick.png",
            FileHash = "hash1",
            SubmittedBy = "alice",
        };

        await _storage.SavePendingAsync(sub, CancellationToken.None);
        var loaded = await _storage.LoadPendingAsync(CancellationToken.None);

        Assert.Single(loaded);
        Assert.Equal("sub-001", loaded[0].SubmitId);
        Assert.Equal("brick.png", loaded[0].FileName);
    }

    [Fact]
    public async Task LoadPending_ignores_non_json_files()
    {
        _nas.SeedFile("StellarHubShared/pending/readme.txt", "not json");
        var sub = new PendingSubmission { SubmitId = "s1", FileName = "a.png" };
        await _storage.SavePendingAsync(sub, CancellationToken.None);

        var loaded = await _storage.LoadPendingAsync(CancellationToken.None);

        Assert.Single(loaded);
        Assert.Equal("s1", loaded[0].SubmitId);
    }

    [Fact]
    public async Task DeletePending_removes_file()
    {
        var sub = new PendingSubmission { SubmitId = "del-1", FileName = "a.png" };
        await _storage.SavePendingAsync(sub, CancellationToken.None);

        await _storage.DeletePendingAsync("del-1", CancellationToken.None);

        var loaded = await _storage.LoadPendingAsync(CancellationToken.None);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task EnsureSharedStructure_creates_directories()
    {
        await _storage.EnsureSharedStructureAsync(CancellationToken.None);

        // No exception = success. Verify we can now write into the structure.
        await _storage.SaveAccountsAsync([], CancellationToken.None);
        var text = _nas.ReadStoredText("StellarHubShared/accounts.json");
        Assert.Contains("\"accounts\"", text);
    }
}
