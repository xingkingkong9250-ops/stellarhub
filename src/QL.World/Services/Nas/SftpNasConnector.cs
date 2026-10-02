using System.IO;
using QL.World.Services.Nas.Sftp;

namespace QL.World.Services.Nas;

/// <summary>
/// M9-A: SFTP implementation of <see cref="INasConnector"/>. Mirrors the
/// structure and semantics of <see cref="Smb.SmbNasConnector"/> but drives a
/// connected <see cref="ISftpSession"/> (SSH.NET in production, a local-dir
/// fake in tests) instead of UNC paths.
///
/// Path semantics: the spec's <see cref="NasConnectionSpec.RootPath"/> is
/// normalized to an absolute POSIX path (<c>/</c> or <c>/volume1/sub</c>).
/// Caller-facing <c>remotePath</c> values are forward-slash paths relative to
/// that root (<c>/</c> for the root), exactly like the WebDAV/SMB connectors,
/// so all higher layers (RemoteLibraryScanner, SharedPaths, download queue,
/// migration) work unchanged.
///
/// Move uses the SFTP POSIX rename extension (atomic server-side replace);
/// servers without it trigger the same download → upload → delete fallback
/// the other connectors use.
/// </summary>
public sealed class SftpNasConnector : INasConnector
{
    private readonly NasConnectionSpec _spec;
    private readonly ISftpSession _session;
    private readonly string _root;
    private bool _disposed;

    internal SftpNasConnector(NasConnectionSpec spec, ISftpSession session, string? rootPath = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _root = NormalizeRoot(rootPath ?? spec.RootPath);
    }

    public NasConnectionSpec Spec => _spec;

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        // Factory connects eagerly; a stat of the configured root proves the
        // whole chain (TCP/SSH/auth/root path). A missing root surfaces as
        // NotFound so the user fixes the RootPath rather than chasing "network".
        return TestCoreAsync(cancellationToken);
    }

    private async Task<bool> TestCoreAsync(CancellationToken cancellationToken)
    {
        await _session.StatAsync(_root, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string absolute = ResolvePath(remotePath);
        IReadOnlyList<SftpListEntry> entries =
            await _session.ListAsync(absolute, cancellationToken).ConfigureAwait(false);

        var list = new List<NasFileInfo>(entries.Count);
        foreach (SftpListEntry e in entries)
        {
            list.Add(new NasFileInfo
            {
                Path = ToRelative(e.FullName),
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Size = e.Length,
                ModifiedAt = e.LastWriteTimeUtc,
            });
        }
        return list;
    }

    public async Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string absolute = ResolvePath(remotePath);
        SftpListEntry e = await _session.StatAsync(absolute, cancellationToken).ConfigureAwait(false);
        return new NasFileInfo
        {
            Path = ToRelative(e.FullName),
            Name = e.Name,
            IsDirectory = e.IsDirectory,
            Size = e.Length,
            ModifiedAt = e.LastWriteTimeUtc,
        };
    }

    public Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _session.OpenReadAsync(ResolvePath(remotePath), cancellationToken);
    }

    public Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _session.DownloadToFileAsync(ResolvePath(remotePath), destinationPath, cancellationToken);
    }

    public async Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        ThrowIfDisposed();

        // SFTP reads are random-access (SftpFileStream supports Seek), so the
        // resume/416 path works identically to the SMB connector.
        await using Stream source = await _session
            .OpenReadAsync(ResolvePath(remotePath), cancellationToken)
            .ConfigureAwait(false);
        source.Seek(offset, SeekOrigin.Begin);

        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await source.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        if (read < length)
            throw new NasConnectionException(
                NasConnectionKind.RangeNotSatisfiable,
                $"only {read} bytes available at offset={offset} length={length}");
        return buffer;
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string absolute = ResolvePath(remotePath);
        await EnsureParentDirectoryAsync(absolute, cancellationToken).ConfigureAwait(false);
        await _session.UploadFromFileAsync(localPath, absolute, cancellationToken).ConfigureAwait(false);
    }

    public async Task MakeDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await EnsureDirectoryAsync(ResolvePath(remotePath), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string absolute = ResolvePath(remotePath);

        SftpListEntry entry;
        try
        {
            entry = await _session.StatAsync(absolute, cancellationToken).ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            return; // already gone — idempotent success
        }

        try
        {
            if (entry.IsDirectory)
                await _session.DeleteDirectoryAsync(absolute, cancellationToken).ConfigureAwait(false);
            else
                await _session.DeleteFileAsync(absolute, cancellationToken).ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            // Vanished between stat and delete.
        }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string source = ResolvePath(sourcePath);
        string destination = ResolvePath(destinationPath);

        try
        {
            await _session.PosixRenameAsync(source, destination, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            throw;
        }
        catch (NasConnectionException)
        {
            // No POSIX rename (older/limited server) or cross-device:
            // fall back to download → upload → delete, same as WebDAV/SMB.
        }

        string tempPath = Path.Combine(Path.GetTempPath(), "stellarhub-sftp-move-" + Guid.NewGuid().ToString("N"));
        try
        {
            await _session.DownloadToFileAsync(source, tempPath, cancellationToken).ConfigureAwait(false);
            await UploadFileAsync(destinationPath, tempPath, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _session.Dispose();
        }
        catch
        {
            // Dispose never throws.
        }
    }

    // ─── path helpers ───────────────────────────────────────────────────────

    private string ResolvePath(string remotePath)
    {
        string rel = string.IsNullOrEmpty(remotePath) ? string.Empty : remotePath.Trim();
        if (rel.Length > 0 && rel[0] == '/')
            rel = rel[1..];
        if (rel.Length == 0)
            return _root;
        return _root == "/" ? "/" + rel : _root + "/" + rel;
    }

    private string ToRelative(string absolutePath)
    {
        string full = absolutePath.TrimEnd('/');
        if (_root == "/")
            return full.Length == 0 ? "/" : full;
        if (string.Equals(full, _root, StringComparison.Ordinal))
            return "/";
        if (full.StartsWith(_root + "/", StringComparison.Ordinal))
            return full[_root.Length..]; // keeps leading '/'
        return absolutePath;
    }

    /// <summary>
    /// Creates every missing segment from the configured root up to
    /// <paramref name="targetAbsolutePath"/>. Already-present segments are
    /// skipped (SFTP mkdir throws if the directory exists).
    /// </summary>
    private async Task EnsureDirectoryAsync(string targetAbsolutePath, CancellationToken cancellationToken)
    {
        string relative = ToRelative(targetAbsolutePath).Trim('/');
        string current = _root;
        if (relative.Length == 0)
            return; // target IS the root, assumed to exist

        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current == "/" ? "/" + segment : current + "/" + segment;
            if (!await _session.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                await _session.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task EnsureParentDirectoryAsync(string absoluteFilePath, CancellationToken cancellationToken)
    {
        int slash = absoluteFilePath.LastIndexOf('/');
        string parent = slash <= 0 ? "/" : absoluteFilePath[..slash];
        return EnsureDirectoryAsync(parent, cancellationToken);
    }

    private static string NormalizeRoot(string rootPath)
    {
        string root = string.IsNullOrWhiteSpace(rootPath) ? "/" : rootPath.Trim();
        if (!root.StartsWith('/'))
            root = "/" + root; // SFTP requires absolute paths
        root = root.TrimEnd('/');
        return root.Length == 0 ? "/" : root;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SftpNasConnector));
    }
}
