using System.IO;

namespace QL.World.Services.Nas;

/// <summary>
/// <see cref="IFileSystemAccessor"/> backed by <see cref="System.IO"/> APIs.
/// Preserves the exact enumeration (<see cref="EnumerationOptions"/> with
/// <see cref="EnumerationOptions.IgnoreInaccessible"/>, Hidden/System skip),
/// read (<see cref="FileOptions.SequentialScan"/> + 1 MB buffer) and size
/// semantics the pre-M5 <c>ImportService</c> used directly, so local imports
/// are byte-for-byte unchanged after the <see cref="IFileSystemAccessor"/>
/// refactor. Singleton: stateless, safe to share.
/// </summary>
public sealed class LocalFileSystemAccessor : IFileSystemAccessor
{
    private const int STREAM_BUFFER_SIZE = 1024 * 1024;

    public static LocalFileSystemAccessor Instance { get; } = new();

    private LocalFileSystemAccessor() { }

    public bool FileExists(string path) => File.Exists(path);

    public NasFileInfo? GetFileInfo(string path)
    {
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return new NasFileInfo
            {
                Path = path,
                Name = info.Name,
                IsDirectory = false,
                Size = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
            };
        }

        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            return new NasFileInfo
            {
                Path = path,
                Name = info.Name,
                IsDirectory = true,
                Size = 0,
                ModifiedAt = info.LastWriteTimeUtc,
            };
        }

        return null;
    }

    public Stream OpenRead(string path) => new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        bufferSize: STREAM_BUFFER_SIZE, FileOptions.SequentialScan);

    public IEnumerable<string> EnumerateFiles(
        string rootPath,
        IReadOnlySet<string> extensions,
        bool recurseSubdirectories,
        bool skipHidden)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = recurseSubdirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = skipHidden
                ? FileAttributes.Hidden | FileAttributes.System
                : FileAttributes.System,
        };

        string full = Path.GetFullPath(rootPath);
        if (File.Exists(full))
        {
            if (ExtensionMatches(full, extensions))
                yield return full;
            yield break;
        }

        if (!Directory.Exists(full))
            yield break;

        foreach (string file in Directory.EnumerateFiles(full, "*", enumeration))
        {
            if (ExtensionMatches(file, extensions))
                yield return file;
        }
    }

    public long GetFileSize(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : -1;
    }

    private static bool ExtensionMatches(string path, IReadOnlySet<string> extensions)
        => extensions.Contains(Path.GetExtension(path));
}
