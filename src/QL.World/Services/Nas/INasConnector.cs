using System.IO;

namespace QL.World.Services.Nas;

/// <summary>
/// Abstraction over a NAS connection. M5-B ships the WebDAV implementation
/// (<c>WebDavNasConnector</c>); SMB/SFTP drivers are reserved for later
/// milestones — the signatures below are protocol-agnostic so the same
/// <see cref="IFileSystemAccessor"/> / <see cref="RemoteLibraryScanner"/>
/// (M5-D) code paths work against any driver.
///
/// Implementations must be safe to call from a background thread; never
/// invoke them on the UI thread (WebDAV I/O is blocking at the HTTP layer).
/// </summary>
public interface INasConnector : IDisposable
{
    /// <summary>Connection spec this instance was built from.</summary>
    NasConnectionSpec Spec { get; }

    /// <summary>
    /// Lightweight connectivity + auth probe (WebDAV: PROPFIND root with
    /// <c>Depth: 0</c>). Returns <c>true</c> on success; throws
    /// <see cref="NasConnectionException"/> (Kind =
    /// <see cref="NasConnectionKind.AuthenticationFailed"/> /
    /// <see cref="NasConnectionKind.NetworkUnreachable"/> /
    /// <see cref="NasConnectionKind.Timeout"/>) on failure.
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists immediate children of <paramref name="remotePath"/> (one level deep).
    /// <paramref name="remotePath"/> is normalized forward-slash path relative
    /// to <see cref="NasConnectionSpec.RootPath"/>; pass <c>"/"</c> for the root.
    /// </summary>
    Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>
    /// Returns metadata for a single file/directory. Throws
    /// <see cref="NasConnectionException"/> (Kind =
    /// <see cref="NasConnectionKind.NotFound"/>) when the path does not exist.
    /// </summary>
    Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a sequential read stream over the remote file. Caller disposes
    /// the returned <see cref="Stream"/>. Used by RemoteFileSystemAccessor.OpenRead
    /// and the streaming-MD5 hash path in ImportService (M5-D) so large originals
    /// never have to materialize as a full <c>byte[]</c> in memory.
    /// </summary>
    Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the full file into <paramref name="destinationPath"/>.
    /// Used for original-image fetches (M5-G download queue, M5-E thumbnail
    /// source for very small previews). Overwrites any existing destination file.
    /// </summary>
    Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a byte range [<paramref name="offset"/>,
    /// <paramref name="offset"/>+<paramref name="length"/>) into a newly
    /// allocated buffer. Protocol-agnostic: WebDAV sends an HTTP
    /// <c>Range: bytes=offset-(offset+length-1)</c> header, SMB/SFTP use
    /// Seek/Read. <paramref name="length"/> is bounded by the caller (typically
    /// 1–4 MB) so a <c>byte[]</c> return is safe.
    /// Implementations MUST construct a fresh request per call —
    /// <see cref="System.Net.Http.HttpRequestMessage"/> instances are not
    /// reusable across sends (M3-D lesson).
    /// </summary>
    Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken);

    /// <summary>Uploads a local file to <paramref name="remotePath"/> (WebDAV PUT).</summary>
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a remote directory (WebDAV MKCOL). Idempotent: must NOT throw
    /// when the directory already exists (some servers return 405; treat as success).
    /// </summary>
    Task MakeDirectoryAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a remote file or empty directory (WebDAV DELETE). Idempotent:
    /// must NOT throw when the path is already gone (404 treated as success).
    /// </summary>
    Task DeleteAsync(string remotePath, CancellationToken cancellationToken);
}
