using System.IO;
using QL.World.Services.Nas.Smb;

namespace QL.World.Services.Nas;

/// <summary>
/// SMB implementation of <see cref="INasConnector"/>. The factory establishes
/// a Win32 network session via <see cref="SmbNative.WNetAddConnection2"/>
/// (CONNECT_TEMPORARY) and passes the resolved UNC root to this connector;
/// <see cref="System.IO.File"/> / <see cref="System.IO.Directory"/> then
/// drive UNC paths transparently through the Windows SMB client. Zero new
/// NuGet.
///
/// Path semantics: <paramref name="uncRoot"/> is the absolute UNC path
/// (<c>\\host\share</c> or <c>\\host\share\sub</c>) computed once by
/// <see cref="SmbConnectorFactory"/>. <see cref="ResolveUncPath"/> joins the
/// caller's relative <c>remotePath</c> (forward slashes, rooted at the
/// spec's <see cref="NasConnectionSpec.RootPath"/>) onto <c>uncRoot</c> with
/// backslashes, applying the <c>\\?\UNC\</c> long-path prefix when the
/// total length exceeds 260 to bypass MAX_PATH (M7-B risk mitigation).
///
/// Test bypass: tests construct with <c>ownsConnection:false</c> and point
/// <paramref name="uncRoot"/> at a local temp directory — File/Directory
/// APIs behave identically against local and UNC paths, so the contract
/// holds without a real SMB share.
/// </summary>
public sealed class SmbNasConnector : INasConnector
{
    private readonly NasConnectionSpec _spec;
    private readonly string _uncRoot;
    private readonly string? _remoteName; // \\host\share for WNetCancelConnection2
    private readonly bool _ownsConnection;
    private bool _disposed;

    public SmbNasConnector(NasConnectionSpec spec, string uncRoot, string? remoteName, bool ownsConnection)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        if (string.IsNullOrWhiteSpace(uncRoot))
            throw new ArgumentException("uncRoot must not be empty", nameof(uncRoot));
        _uncRoot = uncRoot;
        _remoteName = remoteName;
        _ownsConnection = ownsConnection;
    }

    public NasConnectionSpec Spec => _spec;

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            if (Directory.Exists(_uncRoot))
                return Task.FromResult(true);
            // Directory.Exists on a non-existent UNC root returns false without
            // throwing (the SMB client swallows DNS/TCP/auth failures as "not
            // found"). Surface as NetworkUnreachable — the host/share is not
            // reachable from this machine with these credentials.
            throw new NasConnectionException(
                NasConnectionKind.NetworkUnreachable,
                $"smb root not reachable: {_uncRoot}");
        }
        catch (NasConnectionException) { throw; }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            var list = new List<NasFileInfo>();
            foreach (string dir in Directory.GetDirectories(unc))
            {
                var di = new DirectoryInfo(dir);
                list.Add(new NasFileInfo
                {
                    Path = NormalizeForward(_uncRoot, dir),
                    Name = di.Name,
                    IsDirectory = true,
                    Size = 0,
                    ModifiedAt = di.LastWriteTimeUtc,
                });
            }
            foreach (string file in Directory.GetFiles(unc))
            {
                var fi = new FileInfo(file);
                list.Add(new NasFileInfo
                {
                    Path = NormalizeForward(_uncRoot, file),
                    Name = fi.Name,
                    IsDirectory = false,
                    Size = fi.Length,
                    ModifiedAt = fi.LastWriteTimeUtc,
                });
            }
            return Task.FromResult<IReadOnlyList<NasFileInfo>>(list);
        }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            var di = new DirectoryInfo(unc);
            if (di.Exists)
            {
                return Task.FromResult(new NasFileInfo
                {
                    Path = NormalizeForward(_uncRoot, unc),
                    Name = di.Name,
                    IsDirectory = true,
                    Size = 0,
                    ModifiedAt = di.LastWriteTimeUtc,
                });
            }
            var fi = new FileInfo(unc);
            if (!fi.Exists)
                throw new NasConnectionException(NasConnectionKind.NotFound, $"not found: {unc}");
            return Task.FromResult(new NasFileInfo
            {
                Path = NormalizeForward(_uncRoot, unc),
                Name = fi.Name,
                IsDirectory = false,
                Size = fi.Length,
                ModifiedAt = fi.LastWriteTimeUtc,
            });
        }
        catch (NasConnectionException) { throw; }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            Stream stream = new FileStream(unc, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true);
            return Task.FromResult(stream);
        }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public async Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            await using (Stream src = new FileStream(unc, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true))
            using (Stream dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, cancellationToken);
            }
        }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public async Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            await using Stream src = new FileStream(unc, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true);
            src.Seek(offset, SeekOrigin.Begin);
            byte[] buffer = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await src.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
                if (n == 0) break;
                read += n;
            }
            if (read < length)
                throw new NasConnectionException(
                    NasConnectionKind.RangeNotSatisfiable,
                    $"only {read} bytes available at offset={offset} length={length}");
            return buffer;
        }
        catch (NasConnectionException) { throw; }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            // File.Copy handles same-volume hardlink fast-path and falls back
            // to streaming across devices automatically.
            File.Copy(localPath, unc, overwrite: true);
            return Task.CompletedTask;
        }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public Task MakeDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            // CreateDirectory is idempotent: existing dir returns the instance, no throw.
            Directory.CreateDirectory(unc);
            return Task.CompletedTask;
        }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string unc = ResolveUncPath(remotePath);
        try
        {
            if (File.Exists(unc))
                File.Delete(unc);
            else if (Directory.Exists(unc))
                Directory.Delete(unc, recursive: false);
            // Path already gone — idempotent success.
            return Task.CompletedTask;
        }
        catch (DirectoryNotFoundException) { return Task.CompletedTask; }
        catch (FileNotFoundException) { return Task.CompletedTask; }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string src = ResolveUncPath(sourcePath);
        string dst = ResolveUncPath(destinationPath);
        try
        {
            if (File.Exists(src))
                File.Move(src, dst);
            else if (Directory.Exists(src))
                Directory.Move(src, dst);
            else
                throw new NasConnectionException(NasConnectionKind.NotFound, $"not found: {src}");
        }
        catch (NasConnectionException) { throw; }
        catch (IOException)
        {
            // Cross-volume move fails with IOException — degrade to
            // download/upload/delete (same fallback as WebDav MOVE).
            await UploadFileAsync(src, dst, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { throw MapIoException(ex); }
    }

    private string ResolveUncPath(string remotePath)
    {
        // remotePath is forward-slash relative to RootPath ("/" or "/sub/file").
        string rel = string.IsNullOrEmpty(remotePath) ? string.Empty : remotePath.Trim();
        if (rel.Length > 0 && rel[0] == '/')
            rel = rel[1..];
        rel = rel.Replace('/', '\\');
        string full = string.IsNullOrEmpty(rel) ? _uncRoot : $@"{_uncRoot}\{rel}";
        // Long-path prefix only when needed (>260) to avoid .NET API quirks
        // with the prefix on short paths.
        if (full.Length > 260 && full.StartsWith(@"\\") && !full.StartsWith(@"\\?\"))
            return @"\\?\UNC\" + full.Substring(2);
        return full;
    }

    private static string NormalizeForward(string uncRoot, string fullPath)
    {
        // Returns the forward-slash path relative to uncRoot, matching WebDav
        // connector output convention (caller passes "/" for root).
        string root = uncRoot.TrimEnd('\\', '/');
        string full = fullPath.TrimEnd('\\', '/');
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return "/";
        if (full.Length > root.Length &&
            full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string rel = full[root.Length..].Replace('\\', '/');
            if (!rel.StartsWith('/'))
                rel = "/" + rel;
            return rel;
        }
        return fullPath.Replace('\\', '/');
    }

    private static NasConnectionException MapIoException(Exception ex)
    {
        // .NET IOException / UnauthorizedAccessException HResult is 0x8007XXXX
        // (FACILITY_WIN32); the low 16 bits hold the Win32 error code.
        int win32 = ex.HResult & 0xFFFF;
        return win32 switch
        {
            SmbNative.ERROR_LOGON_FAILURE or
            SmbNative.ERROR_INVALID_PASSWORD or
            SmbNative.ERROR_ACCESS_DENIED or
            SmbNative.ERROR_SESSION_CREDENTIAL_EXPIRED =>
                new(NasConnectionKind.AuthenticationFailed, $"smb auth failed: {ex.Message}", ex),
            SmbNative.ERROR_BAD_NETPATH or
            SmbNative.ERROR_NETWORK_UNREACHABLE =>
                new(NasConnectionKind.NetworkUnreachable, $"smb network unreachable: {ex.Message}", ex),
            SmbNative.ERROR_FILE_NOT_FOUND or
            SmbNative.ERROR_PATH_NOT_FOUND =>
                new(NasConnectionKind.NotFound, $"smb not found: {ex.Message}", ex),
            _ => new(NasConnectionKind.ProtocolError, $"smb io error: {ex.Message}", ex),
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SmbNasConnector));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsConnection && _remoteName is not null)
        {
            try
            {
                SmbNative.WNetCancelConnection2(
                    _remoteName,
                    SmbNative.CONNECT_TEMPORARY | SmbNative.CONNECT_UPDATE_PROFILE,
                    fForce: true);
            }
            catch
            {
                // Dispose never throws.
            }
        }
    }
}
