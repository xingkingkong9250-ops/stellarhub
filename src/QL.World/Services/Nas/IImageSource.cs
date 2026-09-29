using System.IO;

namespace QL.World.Services.Nas;

/// <summary>
/// Abstraction over the bytes behind a gallery item so
/// <see cref="ThumbnailService"/> can generate WebP thumbnails from either
/// a local file or a remote (NAS) source without knowing the protocol.
/// M5 ships two implementations: <see cref="LocalImageSource"/> (wraps a
/// Windows path) and <see cref="RemoteImageSource"/> (wraps an
/// <see cref="INasConnector"/> + remote path).
///
/// M5 remote path: download the full file to a temp directory, decode, save
/// WebP, delete temp. M6+ will add Range partial-decode so only the bytes
/// needed for a 256px thumbnail cross the wire.
/// </summary>
public interface IImageSource
{
    /// <summary>
    /// <c>true</c> for <see cref="LocalImageSource"/>; <c>false</c> for any
    /// remote source. Drives the fast <c>Image.Load(path)</c> branch in
    /// <see cref="ThumbnailService.ProcessJob"/>.
    /// </summary>
    bool IsLocal { get; }

    /// <summary>
    /// Human-readable identifier for logs/diagnostics. Local: the file path;
    /// remote: typically the canonical <c>nas://{id}{remotePath}</c> stored in
    /// <c>images.file_path</c>.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Local filesystem path when <see cref="IsLocal"/> is <c>true</c>;
    /// <c>null</c> for remote sources. <see cref="ThumbnailService"/> uses
    /// this for the fast <c>Image.Load(path)</c> path + MagickDecoder fallback
    /// (tga/exr/hdr/psd containers ImageSharp cannot identify). Remote
    /// sources return <c>null</c> and go through the temp-file download path.
    /// </summary>
    string? LocalPath { get; }

    /// <summary>
    /// Opens a sequential read stream over the source. Local:
    /// <c>File.OpenRead</c>; remote: <see cref="INasConnector.OpenReadAsync"/>.
    /// Caller disposes the returned <see cref="Stream"/>. Used by the remote
    /// temp-file download path in <see cref="ThumbnailService.ProcessJob"/>.
    /// </summary>
    Task<Stream> OpenReadAsync(CancellationToken cancellationToken);
}
