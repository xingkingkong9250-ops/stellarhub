using System.IO;
using Renci.SshNet.Sftp;

namespace QL.World.Services.Nas.Sftp;

/// <summary>
/// M9-A: production <see cref="ISftpSession"/> backed by SSH.NET's
/// <see cref="Renci.SshNet.SftpClient"/>. The client (with its
/// <c>ConnectionInfo</c>, credentials and host-key handler) is constructed by
/// <c>SftpConnectorFactory</c> (M9-B); this class only owns call/translate
/// semantics. Every Renci exception is funneled through
/// <see cref="SftpExceptionMap"/> so callers only ever see
/// <see cref="NasConnectionException"/> / <see cref="OperationCanceledException"/>.
///
/// Threading note: SSH.NET exposes async overloads for connect/list/stat/
/// transfer but <c>OpenRead</c> and POSIX <c>RenameFile</c> are sync-only;
/// those are wrapped in <see cref="Task.Run(System.Func{Task}?)"/> so the
/// connector stays off the UI thread regardless.
/// </summary>
internal sealed class RenciSftpSession : ISftpSession
{
    private readonly Renci.SshNet.SftpClient _client;
    private bool _disposed;

    public RenciSftpSession(Renci.SshNet.SftpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsConnected => !_disposed && _client.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client.IsConnected) return Task.CompletedTask;

        // Cancellation fallback: a token trigger during handshake disconnects
        // the underlying session so ConnectAsync unblocks instead of waiting
        // for the full TCP/SSH timeout.
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                var client = (Renci.SshNet.SftpClient)state!;
                try { if (client.IsConnected) client.Disconnect(); }
                catch { /* best-effort */ }
            },
            _client);

        return GuardedAsync(
            async () =>
            {
                try
                {
                    await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await registration.DisposeAsync().ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<SftpListEntry>> ListAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var list = new List<SftpListEntry>();
        await GuardedAsync(
            async () =>
            {
                await foreach (ISftpFile file in _client
                    .ListDirectoryAsync(absolutePath, cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    // SFTP listings always include the server's "." / ".." entries.
                    if (file.Name is "." or "..") continue;
                    list.Add(new SftpListEntry(
                        file.Name,
                        file.FullName,
                        file.IsDirectory,
                        file.Length,
                        file.LastWriteTimeUtc));
                }
            },
            cancellationToken).ConfigureAwait(false);
        return list;
    }

    public Task<SftpListEntry> StatAsync(string absolutePath, CancellationToken cancellationToken)
        => GuardedAsync(
            async () =>
            {
                SftpFileAttributes attr = await _client
                    .GetAttributesAsync(absolutePath, cancellationToken)
                    .ConfigureAwait(false);
                return ToEntry(absolutePath, attr);
            },
            cancellationToken);

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
        => GuardedAsync(
            () => Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return (Stream)_client.OpenRead(absolutePath);
                },
                cancellationToken),
            cancellationToken);

    public Task DownloadToFileAsync(string absolutePath, string localPath, CancellationToken cancellationToken)
        => GuardedAsync(
            async () =>
            {
                await using var destination = new FileStream(
                    localPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true);
                await _client.DownloadFileAsync(absolutePath, destination, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    public Task UploadFromFileAsync(string localPath, string absolutePath, CancellationToken cancellationToken)
        => GuardedAsync(
            async () =>
            {
                await using var source = new FileStream(
                    localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, useAsync: true);
                // Explicit canOverride:true (positional arg) — overwrite
                // semantics matching the WebDAV PUT / SMB File.Copy(overwrite:
                // true) connectors; no progress callback.
                await _client.UploadFileAsync(
                    source, absolutePath, true,
                    (IProgress<Renci.SshNet.UploadFileProgressReport>?)null,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task CreateDirectoryAsync(string absolutePath, CancellationToken cancellationToken)
        => GuardedAsync(
            () => _client.CreateDirectoryAsync(absolutePath, cancellationToken),
            cancellationToken);

    public Task DeleteFileAsync(string absolutePath, CancellationToken cancellationToken)
        => GuardedAsync(
            () => _client.DeleteFileAsync(absolutePath, cancellationToken),
            cancellationToken);

    public Task DeleteDirectoryAsync(string absolutePath, CancellationToken cancellationToken)
        => GuardedAsync(
            () => _client.DeleteDirectoryAsync(absolutePath, cancellationToken),
            cancellationToken);

    public Task PosixRenameAsync(string sourceAbsolutePath, string destinationAbsolutePath, CancellationToken cancellationToken)
        => GuardedAsync(
            () => Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Third arg is isPosix (NOT overwrite!): the POSIX rename
                    // extension atomically replaces an existing destination on
                    // modern servers (OpenSSH 6.2+).
                    _client.RenameFile(sourceAbsolutePath, destinationAbsolutePath, isPosix: true);
                },
                cancellationToken),
            cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _client.Dispose();
        }
        catch
        {
            // Dispose never throws.
        }
    }

    private static SftpListEntry ToEntry(string absolutePath, SftpFileAttributes attr)
    {
        string trimmed = absolutePath.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return new SftpListEntry(
            name.Length == 0 ? "/" : name,
            absolutePath,
            attr.IsDirectory,
            attr.Size,
            attr.LastWriteTimeUtc);
    }

    private static async Task GuardedAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SftpExceptionMap.Map(ex);
        }
    }

    private static async Task<T> GuardedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SftpExceptionMap.Map(ex);
        }
    }
}
