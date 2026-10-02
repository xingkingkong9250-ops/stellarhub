using System.IO;

namespace QL.World.Services.Nas.Sftp;

/// <summary>
/// M9-A: narrow abstraction over an SFTP client session. Production
/// implementation <see cref="RenciSftpSession"/> wraps SSH.NET's
/// <c>SftpClient</c>; tests use a local-directory fake. All methods are
/// async with <see cref="CancellationToken"/>; transport failures are
/// translated into <see cref="NasConnectionException"/> (with a classified
/// <see cref="NasConnectionKind"/>) so the connector layer stays free of
/// third-party type references.
///
/// Paths are absolute server paths (forward slashes, rooted at <c>/</c>);
/// <see cref="SftpNasConnector"/> is responsible for joining the spec's
/// RootPath onto the caller's relative paths before calling this interface.
/// </summary>
internal interface ISftpSession : IDisposable
{
    /// <summary>True after a successful ConnectAsync and before disconnect/dispose.</summary>
    bool IsConnected { get; }

    /// <summary>Establishes the SSH+SFTP connection and authenticates.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists immediate children. The SFTP server's own <c>.</c>/<c>..</c>
    /// entries are filtered by the implementation. Throws
    /// <see cref="NasConnectionException"/> (NotFound) when the directory
    /// does not exist.
    /// </summary>
    Task<IReadOnlyList<SftpListEntry>> ListAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Returns metadata for a file or directory; throws NotFound when absent.</summary>
    Task<SftpListEntry> StatAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Non-throwing existence probe.</summary>
    Task<bool> ExistsAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a readable, seekable stream over the remote file. Caller
    /// disposes the stream; the session (and its connection) must stay alive
    /// for the stream's lifetime.
    /// </summary>
    Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Downloads the full remote file into <paramref name="localPath"/> (overwrite).</summary>
    Task DownloadToFileAsync(string absolutePath, string localPath, CancellationToken cancellationToken);

    /// <summary>Uploads a local file to the remote path, overwriting an existing file.</summary>
    Task UploadFromFileAsync(string localPath, string absolutePath, CancellationToken cancellationToken);

    /// <summary>
    /// Creates one directory segment. Caller ensures parents exist and
    /// treats an already-present directory as success.
    /// </summary>
    Task CreateDirectoryAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Deletes a file. Idempotency (already-gone = success) lives in the connector.</summary>
    Task DeleteFileAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Deletes an empty directory.</summary>
    Task DeleteDirectoryAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>
    /// Server-side rename using the SFTP POSIX rename extension (atomic
    /// replacement of an existing destination on servers that support it,
    /// e.g. OpenSSH 6.2+). Throws on unsupported servers / cross-device
    /// cases — the connector then falls back to download/upload/delete.
    /// </summary>
    Task PosixRenameAsync(string sourceAbsolutePath, string destinationAbsolutePath, CancellationToken cancellationToken);
}
