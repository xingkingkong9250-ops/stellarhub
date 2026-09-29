using System.IO;

namespace QL.World.Services.Nas;

/// <summary>
/// <see cref="IFileSystemAccessor"/> backed by an <see cref="INasConnector"/>.
/// Canonical stored paths use the <c>nas://{connectionId}{remotePath}</c>
/// scheme so they never collide with local Windows paths and survive dedup
/// against <see cref="LocalFileSystemAccessor"/> yields without branching.
///
/// Synchronous signatures block on <c>INasConnector</c>'s async methods via
/// <see cref="System.Threading.Tasks.ValueTask.GetAwaiter().GetResult()"/>
/// — safe on the import background thread (<see cref="ImportService"/> runs
/// scans inside <c>Task.Run</c>), forbidden on the UI thread.
/// </summary>
public sealed class RemoteFileSystemAccessor : IFileSystemAccessor
{
    private readonly INasConnector _connector;
    private readonly int _connectionId;
    private readonly string _schemePrefix;

    public RemoteFileSystemAccessor(INasConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        _connector = connector;
        _connectionId = connector.Spec.Id;
        _schemePrefix = $"nas://{_connectionId}";
    }

    public bool FileExists(string canonicalPath)
    {
        try
        {
            return GetFileInfo(canonicalPath) is not null;
        }
        catch (NasConnectionException)
        {
            // NotFound is the only expected failure here; auth/timeout bubble up.
            return false;
        }
    }

    public NasFileInfo? GetFileInfo(string canonicalPath)
    {
        string remotePath = ToRemote(canonicalPath);
        try
        {
            NasFileInfo remote = _connector.GetMetadataAsync(remotePath, CancellationToken.None)
                .GetAwaiter().GetResult();
            return WithCanonical(remote);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            return null;
        }
    }

    public Stream OpenRead(string canonicalPath)
    {
        string remotePath = ToRemote(canonicalPath);
        return _connector.OpenReadAsync(remotePath, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public IEnumerable<string> EnumerateFiles(
        string rootCanonical,
        IReadOnlySet<string> extensions,
        bool recurseSubdirectories,
        bool skipHidden)
    {
        // skipHidden is meaningless for WebDAV; recurse is always honored
        // because a scan that stops at the first level defeats the point.
        string rootRemote = ToRemote(rootCanonical);
        foreach (string file in EnumerateRecursive(rootRemote, extensions, recurseSubdirectories))
            yield return file;
    }

    public long GetFileSize(string canonicalPath)
    {
        NasFileInfo? info = GetFileInfo(canonicalPath);
        return info?.Size ?? -1;
    }

    private IEnumerable<string> EnumerateRecursive(
        string remoteDir,
        IReadOnlySet<string> extensions,
        bool recurse)
    {
        IReadOnlyList<NasFileInfo> children = _connector.ListFilesAsync(remoteDir, CancellationToken.None)
            .GetAwaiter().GetResult();

        foreach (NasFileInfo child in children)
        {
            if (child.IsDirectory)
            {
                if (recurse)
                {
                    foreach (string descendant in EnumerateRecursive(child.Path, extensions, recurse))
                        yield return descendant;
                }
                continue;
            }

            if (ExtensionMatches(child.Name, extensions))
                yield return ToCanonical(child.Path);
        }
    }

    private NasFileInfo WithCanonical(NasFileInfo remote) => remote with { Path = ToCanonical(remote.Path) };

    /// <summary>
    /// canonical <c>nas://{id}{remotePath}</c> → remote path rooted at
    /// RootPath. Also accepts a raw remote path (no <c>nas://</c> prefix)
    /// so <see cref="RemoteLibraryScanner"/> can hand <see cref="NasConnectionSpec.RootPath"/>
    /// straight through without knowing the canonical scheme.
    /// </summary>
    private string ToRemote(string canonical)
    {
        if (!canonical.StartsWith(_schemePrefix, StringComparison.Ordinal))
            return canonical; // raw remote path (scanner's RootPath input)
        string rest = canonical[_schemePrefix.Length..];
        return string.IsNullOrEmpty(rest) ? "/" : rest;
    }

    /// <summary>remote path → canonical <c>nas://{id}{remotePath}</c>.</summary>
    private string ToCanonical(string remotePath) => $"{_schemePrefix}{remotePath}";

    private static bool ExtensionMatches(string name, IReadOnlySet<string> extensions)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return false;
        return extensions.Contains(name[dot..]);
    }
}
