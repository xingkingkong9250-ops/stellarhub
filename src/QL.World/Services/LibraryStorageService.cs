using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace QL.World.Services;

/// <summary>
/// M11: content-addressed physical storage (CAS) plus NTFS hardlink
/// creation for the category view layer.
///
/// <para>Physical layer — one unique file per content hash under
/// <c>library\files\&lt;h0&gt;\&lt;h0h1h2h3&gt;\</c>. Re-importing the same
/// bytes (any category, any source path) skips the copy: the hash IS the
/// identity.</para>
///
/// <para>View layer — each <c>images</c> row gets its own NTFS hardlink
/// under <c>library\&lt;一级分类&gt;\</c>. Hardlinks share the same MFT
/// record as the physical file: zero extra disk per category, and the
/// physical file stays alive as long as at least one link exists. Both
/// paths must be on the same NTFS volume — the W7 wizard enforces this via
/// <see cref="LibraryRootResolver.ValidateSameVolume"/>.</para>
///
/// File writes land via a same-directory temp file + atomic move so a
/// crash mid-copy never leaves a truncated permanent file.
/// </summary>
public sealed class LibraryStorageService
{
    private const int StreamBufferSize = 1024 * 1024;

    private readonly Action<string> _log;

    public LibraryStorageService(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>
    /// Copies <paramref name="content"/> into the sharded physical path
    /// unless a file with the same content hash already exists (CAS
    /// dedupe). Returns the physical path. The caller owns the stream;
    /// on a CAS hit the stream is NOT consumed (seek-free — remote
    /// network streams stay at their current position).
    /// </summary>
    public string EnsurePhysical(
        string libraryRoot,
        string contentHash,
        Stream content,
        string originalFileName,
        string entryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryCode);

        string targetPath = LibraryRootResolver.GetPhysicalPath(
            libraryRoot, contentHash, originalFileName, entryCode);

        if (File.Exists(targetPath))
        {
            _log($"[LIBRARY] CAS hit {contentHash} → {targetPath}");
            return targetPath;
        }

        string shardDir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(shardDir);

        string tempPath = Path.Combine(shardDir, $".tmp-{Guid.NewGuid():N}");
        try
        {
            using (var file = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: StreamBufferSize, useAsync: false))
            {
                content.CopyTo(file, StreamBufferSize);
            }

            // Atomic publish: target existence is the CAS-committed marker.
            File.Move(tempPath, targetPath, overwrite: false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        _log($"[LIBRARY] CAS add {contentHash} → {targetPath}");
        return targetPath;
    }

    /// <summary>Stream-from-disk convenience wrapper around <see cref="EnsurePhysical"/>.</summary>
    public string EnsurePhysicalFromFile(
        string libraryRoot,
        string contentHash,
        string sourcePath,
        string entryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        using var stream = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: StreamBufferSize, FileOptions.SequentialScan);
        return EnsurePhysical(
            libraryRoot, contentHash, stream, Path.GetFileName(sourcePath), entryCode);
    }

    /// <summary>
    /// Creates an NTFS hardlink at <paramref name="categoryHardlinkPath"/>
    /// pointing at <paramref name="physicalPath"/>. Idempotent: an
    /// existing link path is treated as success (retry / re-import
    /// recovery). Throws <see cref="IOException"/> on cross-volume or any
    /// Win32 failure so the caller can mark the batch failed.
    /// </summary>
    public string CreateCategoryHardlink(string physicalPath, string categoryHardlinkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryHardlinkPath);

        if (!File.Exists(physicalPath))
            throw new FileNotFoundException(
                "Cannot hardlink: physical CAS file does not exist.", physicalPath);

        if (File.Exists(categoryHardlinkPath))
        {
            _log($"[LIBRARY] Hardlink already exists: {categoryHardlinkPath}");
            return categoryHardlinkPath;
        }

        string? categoryDir = Path.GetDirectoryName(categoryHardlinkPath);
        if (!string.IsNullOrEmpty(categoryDir))
            Directory.CreateDirectory(categoryDir);

        if (!CreateHardLinkW(categoryHardlinkPath, physicalPath, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"CreateHardLink failed (Win32 error {error}) " +
                $"{physicalPath} → {categoryHardlinkPath}: {new Win32Exception(error).Message}",
                new Win32Exception(error));
        }

        _log($"[LIBRARY] Hardlink {physicalPath} → {categoryHardlinkPath}");
        return categoryHardlinkPath;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* temp cleanup is best effort */ }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
