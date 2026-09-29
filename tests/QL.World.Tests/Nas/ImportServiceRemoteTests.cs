using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-D: ImportService remote-mode behavior. A stub
/// <see cref="IFileSystemAccessor"/> stands in for a real
/// <see cref="RemoteFileSystemAccessor"/>: it yields canonical
/// <c>nas://1/...</c> paths and serves a real PNG byte stream so the
/// streaming MD5 + <see cref="ImageInspector.TryReadMetadata(Stream)"/>
/// paths run end-to-end. Verifies <c>images.source = 'remote'</c> and
/// <c>nas_connection_id</c> are written by <c>InsertBatch</c>.
/// </summary>
public sealed class ImportServiceRemoteTests
{
    [Fact]
    public async Task Remote_import_writes_source_remote_and_nas_connection_id()
    {
        using var library = new TestLibrary();
        // Real PNG bytes so ImageInspector.TryReadMetadata(Stream) succeeds.
        byte[] png = File.ReadAllBytes(TestImageFactory.CreatePng(
            Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N")),
            "remote.png",
            width: 14,
            height: 9));

        var accessor = new StubRemoteAccessor(1, "/photos", png, "/photos/remote.png");
        var inspector = new ImageInspector();
        var import = new ImportService(library.Db, inspector);

        var options = new ImportOptions
        {
            Source = ImageSource.Remote,
            NasConnectionId = 1,
            InputPaths = ["/photos"],
            RecurseSubfolders = true,
            SkipHidden = true,
            EnabledExtensions = ImportOptions.SupportedExtensions,
        };

        ImportReport report = await import.ImportAsync(options, accessor, progress: null, CancellationToken.None);

        Assert.Equal(1, report.AddedCount);
        Assert.Equal(0, report.SkippedCount);

        var (path, source, nasId, hash) = library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT file_path, source, nas_connection_id, file_hash FROM images;";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                reader.GetString(3));
        });

        Assert.Equal("nas://1/photos/remote.png", path);
        Assert.Equal("remote", source);
        Assert.Equal(1, nasId);
        Assert.False(string.IsNullOrEmpty(hash));
        // Hash is stable for the same bytes — recompute and compare.
        Assert.Equal(
            QL.World.Helpers.Md5Hasher.ComputeStream(new MemoryStream(png)),
            hash);
    }

    [Fact]
    public async Task Remote_import_skips_corrupt_stream_and_logs_it()
    {
        using var library = new TestLibrary();
        byte[] garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var accessor = new StubRemoteAccessor(1, "/photos", garbage, "/photos/bad.bin");
        var inspector = new ImageInspector();
        var import = new ImportService(library.Db, inspector);

        var options = new ImportOptions
        {
            Source = ImageSource.Remote,
            NasConnectionId = 1,
            InputPaths = ["/photos"],
            RecurseSubfolders = true,
            EnabledExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bin" },
        };

        ImportReport report = await import.ImportAsync(options, accessor, progress: null, CancellationToken.None);

        Assert.Equal(0, report.AddedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Contains(report.LogLines, l => l.Contains("无法解码/已损坏"));
        Assert.Equal(0, library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM images;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }));
    }

    /// <summary>
    /// Minimal <see cref="IFileSystemAccessor"/> standing in for
    /// <see cref="RemoteFileSystemAccessor"/>. <see cref="GetFileInfo"/>
    /// returns a directory for the input root (so ScanFiles descends) and a
    /// file for the registered canonical path; <see cref="OpenRead"/> returns
    /// a fresh <see cref="MemoryStream"/> wrapping the canned bytes (hash +
    /// metadata each get their own stream, mirroring how the real accessor
    /// issues two GETs per remote file).
    /// </summary>
    private sealed class StubRemoteAccessor : IFileSystemAccessor
    {
        private readonly int _connectionId;
        private readonly string _inputRoot;
        private readonly byte[] _bytes;
        private readonly string _remotePath;
        private readonly string _canonical;
        private readonly long _size;

        public StubRemoteAccessor(int connectionId, string inputRoot, byte[] bytes, string remotePath)
        {
            _connectionId = connectionId;
            _inputRoot = inputRoot;
            _bytes = bytes;
            _remotePath = remotePath;
            _canonical = $"nas://{_connectionId}{remotePath}";
            _size = bytes.Length;
        }

        public bool FileExists(string path) => path == _canonical;

        public NasFileInfo? GetFileInfo(string path)
        {
            if (path == _canonical)
                return new NasFileInfo
                {
                    Path = path,
                    Name = Path.GetFileName(_remotePath),
                    IsDirectory = false,
                    Size = _size,
                };
            if (path == _inputRoot)
                return new NasFileInfo
                {
                    Path = path,
                    Name = path.TrimEnd('/'),
                    IsDirectory = true,
                    Size = 0,
                };
            return null;
        }

        public Stream OpenRead(string path) => new MemoryStream(_bytes, writable: false);

        public IEnumerable<string> EnumerateFiles(
            string rootPath,
            IReadOnlySet<string> extensions,
            bool recurseSubdirectories,
            bool skipHidden)
        {
            string ext = Path.GetExtension(_remotePath);
            if (extensions.Contains(ext))
                yield return _canonical;
        }

        public long GetFileSize(string path) => path == _canonical ? _size : -1;
    }
}
