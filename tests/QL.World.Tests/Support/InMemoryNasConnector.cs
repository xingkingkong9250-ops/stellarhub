using System.IO;
using System.Text;
using QL.World.Services.Nas;

namespace QL.World.Tests.Support;

/// <summary>
/// M6-A tests: in-memory INasConnector that stores files in a Dictionary.
/// Supports the subset of operations SharedStorageService uses
/// (OpenRead/Upload/ListFiles/MakeDirectory/Delete). All paths are treated
/// as flat keys — directory listing filters by prefix.
/// </summary>
internal sealed class InMemoryNasConnector : INasConnector
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    public NasConnectionSpec Spec { get; } = new();

    public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken ct)
    {
        string prefix = remotePath.TrimEnd('/') + "/";
        var result = new List<NasFileInfo>();
        foreach (var kv in _files)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                string rest = kv.Key[prefix.Length..];
                if (!rest.Contains('/'))
                {
                    result.Add(new NasFileInfo
                    {
                        Path = kv.Key,
                        Name = rest,
                        IsDirectory = false,
                        Size = kv.Value.Length,
                        ModifiedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
        }
        return Task.FromResult<IReadOnlyList<NasFileInfo>>(result);
    }

    public Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken ct)
    {
        if (_files.TryGetValue(remotePath, out var bytes))
        {
            return Task.FromResult(new NasFileInfo
            {
                Path = remotePath,
                Name = Path.GetFileName(remotePath),
                IsDirectory = false,
                Size = bytes.Length,
                ModifiedAt = DateTimeOffset.UtcNow,
            });
        }
        throw new NasConnectionException(NasConnectionKind.NotFound, "not found");
    }

    public Task<Stream> OpenReadAsync(string remotePath, CancellationToken ct)
    {
        if (_files.TryGetValue(remotePath, out var bytes))
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        throw new NasConnectionException(NasConnectionKind.NotFound, "not found");
    }

    public Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken ct)
    {
        if (_files.TryGetValue(remotePath, out var bytes))
        {
            File.WriteAllBytes(destinationPath, bytes);
            return Task.CompletedTask;
        }
        throw new NasConnectionException(NasConnectionKind.NotFound, "not found");
    }

    public Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct)
    {
        _files[remotePath] = File.ReadAllBytes(localPath);
        return Task.CompletedTask;
    }

    public Task MakeDirectoryAsync(string remotePath, CancellationToken ct)
    {
        _dirs.Add(remotePath);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        _files.Remove(remotePath);
        return Task.CompletedTask;
    }

    /// <summary>Test helper: inject raw bytes for a path.</summary>
    public void SeedFile(string remotePath, string content)
    {
        _files[remotePath] = Encoding.UTF8.GetBytes(content);
    }

    /// <summary>U6-A test helper: inject arbitrary bytes (e.g. encoded WebP).</summary>
    public void SeedBytes(string remotePath, byte[] bytes)
    {
        _files[remotePath] = bytes;
    }

    /// <summary>Test helper: read back stored bytes as string.</summary>
    public string ReadStoredText(string remotePath)
    {
        return _files.TryGetValue(remotePath, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : string.Empty;
    }

    public void Dispose() { }
}
