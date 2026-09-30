using QL.World.Models;

namespace QL.World.Services.Nas;

/// <summary>
/// M6-D: business logic for the NAS-shared library. Reads/writes
/// library.json through <see cref="SharedStorageService"/> and exposes
/// list/add/remove operations. The gallery treats shared images as a
/// separate, read-only-by-default source (users can download copies).
/// </summary>
public sealed class SharedLibraryService
{
    private readonly SharedStorageService _storage;
    private readonly object _lock = new();

    public SharedLibraryService(SharedStorageService storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>Load all shared image records from library.json.</summary>
    public async Task<IReadOnlyList<SharedImageRecord>> LoadAsync(CancellationToken ct)
    {
        lock (_lock) { }
        return await _storage.LoadLibraryAsync(ct);
    }

    /// <summary>
    /// Adds a record to the shared library. If a record with the same Id
    /// (file hash) already exists, it is replaced (no duplicate uploads).
    /// </summary>
    public async Task AddOrUpdateAsync(SharedImageRecord record, CancellationToken ct)
    {
        var all = (await _storage.LoadLibraryAsync(ct)).ToList();
        var idx = all.FindIndex(r => r.Id == record.Id);
        if (idx >= 0)
            all[idx] = record;
        else
            all.Add(record);
        await _storage.SaveLibraryAsync(all, ct);
    }

    /// <summary>Removes a record by its file-hash id.</summary>
    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var all = (await _storage.LoadLibraryAsync(ct)).ToList();
        all.RemoveAll(r => r.Id == id);
        await _storage.SaveLibraryAsync(all, ct);
    }

    /// <summary>True when a record with the given id exists.</summary>
    public async Task<bool> ContainsAsync(string id, CancellationToken ct)
    {
        var all = await _storage.LoadLibraryAsync(ct);
        return all.Any(r => r.Id == id);
    }
}
