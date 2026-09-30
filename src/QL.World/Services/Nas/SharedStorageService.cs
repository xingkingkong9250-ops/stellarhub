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
    private const string SharedDir = "StellarHubShared";
    private const string AccountsFileName = "accounts.json";
    private const string LibraryFileName = "library.json";
    private const string PendingDir = "pending";
    private const string ThumbnailsDir = "thumbnails";

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

    /// <summary>Creates the shared directory structure on the NAS (idempotent).</summary>
    public async Task EnsureSharedStructureAsync(CancellationToken ct)
    {
        await _connector.MakeDirectoryAsync(SharedDir, ct).ConfigureAwait(false);
        await _connector.MakeDirectoryAsync($"{SharedDir}/{PendingDir}", ct).ConfigureAwait(false);
        await _connector.MakeDirectoryAsync($"{SharedDir}/{ThumbnailsDir}", ct).ConfigureAwait(false);
    }

    // ─── accounts.json ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserAccount>> LoadAccountsAsync(CancellationToken ct)
    {
        var wrapper = await ReadJsonAsync<AccountsFile>($"{SharedDir}/{AccountsFileName}", ct).ConfigureAwait(false);
        return wrapper?.Accounts ?? [];
    }

    public async Task SaveAccountsAsync(IReadOnlyList<UserAccount> accounts, CancellationToken ct)
    {
        var wrapper = new AccountsFile { Version = 1, Accounts = accounts.ToList() };
        await WriteJsonAsync($"{SharedDir}/{AccountsFileName}", wrapper, ct).ConfigureAwait(false);
    }

    // ─── library.json ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<SharedImageRecord>> LoadLibraryAsync(CancellationToken ct)
    {
        var wrapper = await ReadJsonAsync<LibraryFile>($"{SharedDir}/{LibraryFileName}", ct).ConfigureAwait(false);
        return wrapper?.Images ?? [];
    }

    public async Task SaveLibraryAsync(IReadOnlyList<SharedImageRecord> images, CancellationToken ct)
    {
        var wrapper = new LibraryFile { Version = 1, Images = images.ToList() };
        await WriteJsonAsync($"{SharedDir}/{LibraryFileName}", wrapper, ct).ConfigureAwait(false);
    }

    // ─── pending/*.json ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<PendingSubmission>> LoadPendingAsync(CancellationToken ct)
    {
        var files = await _connector.ListFilesAsync($"{SharedDir}/{PendingDir}", ct).ConfigureAwait(false);
        var result = new List<PendingSubmission>();
        foreach (var f in files.Where(f => !f.IsDirectory && f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var sub = await ReadJsonAsync<PendingSubmission>($"{SharedDir}/{PendingDir}/{f.Name}", ct).ConfigureAwait(false);
            if (sub is not null)
                result.Add(sub);
        }
        return result;
    }

    public async Task SavePendingAsync(PendingSubmission submission, CancellationToken ct)
    {
        await WriteJsonAsync($"{SharedDir}/{PendingDir}/{submission.SubmitId}.json", submission, ct).ConfigureAwait(false);
    }

    public async Task DeletePendingAsync(string submitId, CancellationToken ct)
    {
        await _connector.DeleteAsync($"{SharedDir}/{PendingDir}/{submitId}.json", ct).ConfigureAwait(false);
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
