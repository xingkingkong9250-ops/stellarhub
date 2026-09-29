using System.IO;

namespace QL.World.Services.Nas;

/// <summary>
/// Unified file-system access abstraction implemented for both local paths
/// (<c>LocalFileSystemAccessor</c>, wrapping <see cref="System.IO"/> APIs —
/// present behavior) and remote paths (<c>RemoteFileSystemAccessor</c>,
/// delegating to <see cref="INasConnector"/> — M5-D). ImportService and
/// ThumbnailService depend on this interface so the same code path handles
/// local and remote sources without branching at call sites.
///
/// Signatures are synchronous on purpose: ImportService.ScanFiles consumes
/// <see cref="EnumerateFiles"/> via <c>foreach</c>/<c>yield</c> over a local
/// <see cref="System.Collections.Generic.IEnumerable{T}"/>; making the contract
/// fully async would force <c>IAsyncEnumerable&lt;T&gt;</c> through the import
/// pipeline and ripple into the SQLite batch writer. The remote accessor
/// blocks its backend thread (M5-D scans run inside <c>Task.Run</c>) via
/// <c>GetAwaiter().GetResult()</c> — safe off the UI thread.
/// </summary>
public interface IFileSystemAccessor
{
    /// <summary>True when the local-or-remote path exists.</summary>
    bool FileExists(string path);

    /// <summary>
    /// Metadata for the path, or <c>null</c> when missing. Never throws for
    /// a non-existent path — callers may probe.
    /// </summary>
    NasFileInfo? GetFileInfo(string path);

    /// <summary>
    /// Opens the file for sequential reading. Caller disposes the returned
    /// <see cref="Stream"/>. Throws <see cref="NasConnectionException"/> /
    /// <see cref="System.IO.FileNotFoundException"/> on missing paths.
    /// </summary>
    Stream OpenRead(string path);

    /// <summary>
    /// Recursively enumerates files whose extension (lowercased, no dot)
    /// is in <paramref name="extensions"/>. Directories are descended into
    /// but not returned. Order is implementation-defined.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string rootPath, IReadOnlySet<string> extensions);

    /// <summary>File size in bytes; <c>-1</c> when unknown.</summary>
    long GetFileSize(string path);
}
