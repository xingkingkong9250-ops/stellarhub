using System.IO;
using QL.World.Services.Nas;
using QL.World.Services.Nas.Sftp;

namespace QL.World.Tests.Nas.Sftp;

/// <summary>
/// M9-A test double for <see cref="ISftpSession"/> backed by a local temp
/// directory — the SFTP analogue of SmbNasConnector's "local temp dir as UNC
/// root" trick. All primitives land on File/Directory, so the connector's
/// full contract (including deep mkdir, overwrite upload, POSIX rename) runs
/// without a real SSH server.
///
/// <paramref name="remoteRoot"/> is the absolute SFTP path the connector
/// operates under ("/" in the contract suite; "/nasroot" for sub-root
/// tests); it maps 1:1 onto <paramref name="localRoot"/>.
/// </summary>
internal sealed class FakeSftpSession : ISftpSession
{
    private readonly string _remoteRoot;
    private readonly string _localRoot;

    /// <summary>When set, every call throws <see cref="NasConnectionException"/> with this kind.</summary>
    public NasConnectionKind? FailKind { get; init; }

    public FakeSftpSession(string remoteRoot, string localRoot)
    {
        _remoteRoot = string.IsNullOrEmpty(remoteRoot) ? "/" : remoteRoot;
        _localRoot = localRoot;
    }

    public bool IsConnected { get; private set; } = true;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        MaybeFail();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SftpListEntry>> ListAsync(string absolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string local = Map(absolutePath);
        if (!Directory.Exists(local))
            throw new NasConnectionException(NasConnectionKind.NotFound, $"fake: no dir {absolutePath}");

        var list = new List<SftpListEntry>();
        foreach (string dir in Directory.GetDirectories(local))
        {
            var di = new DirectoryInfo(dir);
            list.Add(new SftpListEntry(
                ToRemoteName(dir, isDirectory: true),
                ToRemotePath(dir),
                IsDirectory: true, Length: 0,
                LastWriteTimeUtc: di.LastWriteTimeUtc));
        }
        foreach (string file in Directory.GetFiles(local))
        {
            var fi = new FileInfo(file);
            list.Add(new SftpListEntry(
                ToRemoteName(file, isDirectory: false),
                ToRemotePath(file),
                IsDirectory: false, Length: fi.Length,
                LastWriteTimeUtc: fi.LastWriteTimeUtc));
        }
        return Task.FromResult<IReadOnlyList<SftpListEntry>>(list);
    }

    public Task<SftpListEntry> StatAsync(string absolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string local = Map(absolutePath);
        if (Directory.Exists(local))
        {
            var di = new DirectoryInfo(local);
            return Task.FromResult(new SftpListEntry(
                ToRemoteName(local, isDirectory: true), absolutePath,
                IsDirectory: true, Length: 0, LastWriteTimeUtc: di.LastWriteTimeUtc));
        }
        if (File.Exists(local))
        {
            var fi = new FileInfo(local);
            return Task.FromResult(new SftpListEntry(
                ToRemoteName(local, isDirectory: false), absolutePath,
                IsDirectory: false, Length: fi.Length, LastWriteTimeUtc: fi.LastWriteTimeUtc));
        }
        throw new NasConnectionException(NasConnectionKind.NotFound, $"fake: not found {absolutePath}");
    }

    public async Task<bool> ExistsAsync(string absolutePath, CancellationToken cancellationToken)
    {
        try
        {
            await StatAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            return false;
        }
    }

    public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string local = Map(absolutePath);
        if (!File.Exists(local))
            throw new NasConnectionException(NasConnectionKind.NotFound, $"fake: no file {absolutePath}");
        Stream stream = new FileStream(local, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DownloadToFileAsync(string absolutePath, string localPath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string local = Map(absolutePath);
        File.Copy(local, localPath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task UploadFromFileAsync(string localPath, string absolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string local = Map(absolutePath);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.Copy(localPath, local, overwrite: true);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string absolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        Directory.CreateDirectory(Map(absolutePath));
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string absolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string local = Map(absolutePath);
        if (File.Exists(local)) File.Delete(local);
        else throw new NasConnectionException(NasConnectionKind.NotFound, $"fake: no file {absolutePath}");
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string absolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string local = Map(absolutePath);
        if (Directory.Exists(local)) Directory.Delete(local, recursive: false);
        else throw new NasConnectionException(NasConnectionKind.NotFound, $"fake: no dir {absolutePath}");
        return Task.CompletedTask;
    }

    public Task PosixRenameAsync(string sourceAbsolutePath, string destinationAbsolutePath, CancellationToken cancellationToken)
    {
        MaybeFail();
        string src = Map(sourceAbsolutePath);
        string dst = Map(destinationAbsolutePath);
        if (File.Exists(src))
        {
            // POSIX rename atomically replaces an existing file destination.
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
        else if (Directory.Exists(src))
        {
            Directory.Move(src, dst);
        }
        else
        {
            throw new NasConnectionException(NasConnectionKind.NotFound, $"fake: no source {sourceAbsolutePath}");
        }
        return Task.CompletedTask;
    }

    public void Dispose() { }

    private void MaybeFail()
    {
        if (FailKind is { } kind)
            throw new NasConnectionException(kind, $"fake injected failure: {kind}");
    }

    private string Map(string absolutePath)
    {
        string rel;
        if (_remoteRoot == "/")
        {
            rel = absolutePath.TrimStart('/');
        }
        else if (absolutePath == _remoteRoot)
        {
            return _localRoot;
        }
        else
        {
            rel = absolutePath.StartsWith(_remoteRoot + "/", StringComparison.Ordinal)
                ? absolutePath[(_remoteRoot.Length + 1)..]
                : absolutePath.TrimStart('/');
        }
        string suffix = rel.Replace('/', Path.DirectorySeparatorChar);
        return suffix.Length == 0 ? _localRoot : Path.Combine(_localRoot, suffix);
    }

    private string ToRemotePath(string localFullPath)
    {
        string normalized = localFullPath.Replace(Path.DirectorySeparatorChar, '/');
        string rootLocal = _localRoot.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
        if (normalized == rootLocal) return _remoteRoot;
        string rel = normalized.StartsWith(rootLocal + "/", StringComparison.Ordinal)
            ? normalized[rootLocal.Length..]
            : "/" + Path.GetFileName(localFullPath);
        return _remoteRoot == "/" ? rel : _remoteRoot + rel;
    }

    private static string ToRemoteName(string localFullPath, bool isDirectory)
        => Path.GetFileName(localFullPath.TrimEnd(Path.DirectorySeparatorChar));
}
