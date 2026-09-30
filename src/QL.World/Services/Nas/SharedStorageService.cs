using System.IO;
using System.Text.Json;
using QL.World.Models;

namespace QL.World.Services.Nas;

/// <summary>
/// M6-A: reads/writes the shared StellarHub directory on a NAS through an
/// <see cref="INasConnector"/>. All shared data (accounts, library index,
/// pending submissions) lives here as JSON files — no SQLite on the NAS
/// because WebDAV does not support file locking or random writes.
///
/// Paths are relative to the connector's RootPath and always use forward
/// slashes. The shared directory is <c>StellarHubShared/</c>.
/// </summary>
public sealed class SharedStorageService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly INasConnector _connector;

    public SharedStorageService(INasConnector connector)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    /// <summary>U6-A: the underlying connector (shared thumbnail cache reuses it).</summary>
    internal INasConnector Connector => _connector;

    /// <summary>Creates the shared directory structure on the NAS (idempotent).</summary>
    public async Task EnsureSharedStructureAsync(CancellationToken ct)
    {
        await _connector.MakeDirectoryAsync(SharedPaths.Root, ct).ConfigureAwait(false);
        await _connector.MakeDirectoryAsync($"{SharedPaths.Root}/{SharedPaths.PendingDir}", ct).ConfigureAwait(false);
        await _connector.MakeDirectoryAsync($"{SharedPaths.Root}/{SharedPaths.ThumbnailsDir}", ct).ConfigureAwait(false);
        await _connector.MakeDirectoryAsync($"{SharedPaths.Root}/{SharedPaths.OriginalsDir}", ct).ConfigureAwait(false);
        await _connector.MakeDirectoryAsync($"{SharedPaths.Root}/{SharedPaths.PendingDir}/{SharedPaths.PendingFilesDir}", ct).ConfigureAwait(false);
    }

    // ─── accounts.json ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserAccount>> LoadAccountsAsync(CancellationToken ct)
    {
        var wrapper = await ReadJsonAsync<AccountsFile>(SharedPaths.Accounts, ct).ConfigureAwait(false);
        return wrapper?.Accounts ?? [];
    }

    public async Task SaveAccountsAsync(IReadOnlyList<UserAccount> accounts, CancellationToken ct)
    {
        var wrapper = new AccountsFile { Version = 1, Accounts = accounts.ToList() };
        await WriteJsonAsync(SharedPaths.Accounts, wrapper, ct).ConfigureAwait(false);
    }

    // ─── library.json ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<SharedImageRecord>> LoadLibraryAsync(CancellationToken ct)
    {
        var wrapper = await ReadJsonAsync<LibraryFile>(SharedPaths.Library, ct).ConfigureAwait(false);
        return wrapper?.Images ?? [];
    }

    public async Task SaveLibraryAsync(IReadOnlyList<SharedImageRecord> images, CancellationToken ct)
    {
        var wrapper = new LibraryFile { Version = 1, Images = images.ToList() };
        await WriteJsonAsync(SharedPaths.Library, wrapper, ct).ConfigureAwait(false);
    }

    // ─── pending/*.json ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<PendingSubmission>> LoadPendingAsync(CancellationToken ct)
    {
        var files = await _connector.ListFilesAsync($"{SharedPaths.Root}/{SharedPaths.PendingDir}", ct).ConfigureAwait(false);
        var result = new List<PendingSubmission>();
        foreach (var f in files.Where(f => !f.IsDirectory && f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var sub = await ReadJsonAsync<PendingSubmission>(SharedPaths.PendingJson(f.Name[..^5]), ct).ConfigureAwait(false);
            if (sub is not null)
                result.Add(sub);
        }
        return result;
    }

    public async Task SavePendingAsync(PendingSubmission submission, CancellationToken ct)
    {
        await WriteJsonAsync(SharedPaths.PendingJson(submission.SubmitId), submission, ct).ConfigureAwait(false);
    }

    public async Task DeletePendingAsync(string submitId, CancellationToken ct)
    {
        await _connector.DeleteAsync(SharedPaths.PendingJson(submitId), ct).ConfigureAwait(false);
    }

    // ─── JSON helpers ───────────────────────────────────────────────────

    private async Task<T?> ReadJsonAsync<T>(string remotePath, CancellationToken ct) where T : class
    {
        try
        {
            using var stream = await _connector.OpenReadAsync(remotePath, ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            return null;
        }
    }

    private async Task WriteJsonAsync<T>(string remotePath, T value, CancellationToken ct)
    {
        string tmp = Path.GetTempFileName();
        try
        {
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, value, JsonOpts, ct).ConfigureAwait(false);
            }
            await _connector.UploadFileAsync(tmp, remotePath, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // ─── JSON file wrappers ─────────────────────────────────────────────

    private sealed class AccountsFile
    {
        public int Version { get; set; }
        public List<UserAccount> Accounts { get; set; } = [];
    }

    private sealed class LibraryFile
    {
        public int Version { get; set; }
        public List<SharedImageRecord> Images { get; set; } = [];
    }
}
