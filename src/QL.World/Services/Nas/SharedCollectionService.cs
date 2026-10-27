using QL.World.Models;

namespace QL.World.Services.Nas;

/// <summary>
/// PEND-004 (0.18.0): business logic for shared collections. Collections are a
/// lightweight grouping layer over <c>library.json</c>, persisted separately in
/// <c>collections.json</c> through <see cref="SharedStorageService"/>. Membership
/// stores image hashes, so it survives library resubmission and works with the
/// aggregated primary + secondary view.
/// </summary>
public sealed class SharedCollectionService
{
    private readonly SharedStorageService _storage;

    public SharedCollectionService(SharedStorageService storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>All shared collections, in creation order.</summary>
    public async Task<IReadOnlyList<SharedCollection>> LoadAsync(CancellationToken ct)
    {
        var all = await _storage.LoadCollectionsAsync(ct).ConfigureAwait(false);
        return all.OrderBy(c => c.CreatedAt).ToList();
    }

    /// <summary>
    /// Creates a collection. Throws <see cref="InvalidOperationException"/> when a
    /// collection with the same (case-insensitive) name already exists.
    /// </summary>
    public async Task<SharedCollection> CreateAsync(string name, string createdBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string trimmed = name.Trim();

        var all = (await _storage.LoadCollectionsAsync(ct).ConfigureAwait(false)).ToList();
        if (all.Any(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"已存在同名共享收藏夹「{trimmed}」。");

        var created = new SharedCollection
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = trimmed,
            CreatedBy = createdBy ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        all.Add(created);
        await _storage.SaveCollectionsAsync(all, ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Deletes a collection by id; missing ids are a quiet no-op.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var all = (await _storage.LoadCollectionsAsync(ct).ConfigureAwait(false)).ToList();
        if (all.RemoveAll(c => c.Id == id) > 0)
            await _storage.SaveCollectionsAsync(all, ct).ConfigureAwait(false);
    }

    /// <summary>Adds image hashes (idempotent, keeps first-seen order). Returns how many were new.</summary>
    public async Task<int> AddImagesAsync(string id, IReadOnlyCollection<string> imageIds, CancellationToken ct)
    {
        if (imageIds.Count == 0) return 0;
        var all = (await _storage.LoadCollectionsAsync(ct).ConfigureAwait(false)).ToList();
        int idx = all.FindIndex(c => c.Id == id);
        if (idx < 0) return 0;

        var ids = all[idx].ImageIds;
        int added = 0;
        foreach (string hash in imageIds)
        {
            if (string.IsNullOrEmpty(hash) || ids.Contains(hash)) continue;
            ids.Add(hash);
            added++;
        }
        if (added > 0)
            await _storage.SaveCollectionsAsync(all, ct).ConfigureAwait(false);
        return added;
    }

    /// <summary>Removes image hashes from a collection. Returns how many were removed.</summary>
    public async Task<int> RemoveImagesAsync(string id, IReadOnlyCollection<string> imageIds, CancellationToken ct)
    {
        if (imageIds.Count == 0) return 0;
        var all = (await _storage.LoadCollectionsAsync(ct).ConfigureAwait(false)).ToList();
        int idx = all.FindIndex(c => c.Id == id);
        if (idx < 0) return 0;

        int removed = all[idx].ImageIds.RemoveAll(ids => imageIds.Contains(ids));
        if (removed > 0)
            await _storage.SaveCollectionsAsync(all, ct).ConfigureAwait(false);
        return removed;
    }
}
