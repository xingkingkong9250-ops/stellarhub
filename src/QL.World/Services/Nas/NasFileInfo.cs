namespace QL.World.Services.Nas;

/// <summary>
/// Remote file/directory metadata returned by <see cref="INasConnector"/>.
/// Mirrors the subset of <see cref="System.IO.FileInfo"/> /
/// <see cref="System.IO.FileSystemInfo"/> needed by ImportService / ThumbnailService
/// so an <see cref="IFileSystemAccessor"/> implementation can translate between
/// local and remote sources without branching at call sites.
/// </summary>
public sealed record NasFileInfo
{
    /// <summary>
    /// Absolute path on the NAS, normalized to forward slashes and rooted at
    /// the connection's <see cref="NasConnectionSpec.RootPath"/>
    /// (e.g. <c>/photos/2026/img.jpg</c>).
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Last path segment — file or directory name.</summary>
    public required string Name { get; init; }

    /// <summary>True for a directory (collection); false for a regular file.</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>
    /// Size in bytes for files; <c>0</c> for directories; <c>-1</c> when the
    /// server reports no content-length (some WebDAV servers omit it on collections).
    /// </summary>
    public long Size { get; init; }

    /// <summary>Last-modified UTC timestamp reported by the server; null when unknown.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>MIME / Content-Type when reported (e.g. <c>image/jpeg</c>); null when unknown.</summary>
    public string? ContentType { get; init; }
}
