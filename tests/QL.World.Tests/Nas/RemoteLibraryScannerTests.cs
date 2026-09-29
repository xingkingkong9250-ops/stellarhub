using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-D: <see cref="RemoteLibraryScanner"/> end-to-end with a stub
/// <see cref="INasConnector"/>. The stub returns a small directory tree
/// (root → /photos → two PNGs) and serves real PNG bytes from
/// <see cref="INasConnector.OpenReadAsync"/> so the streaming hash +
/// <see cref="ImageInspector.TryReadMetadata(Stream)"/> paths run. Verifies
/// the scanner recurses, calls <see cref="ImportService"/> once per file, and
/// the resulting rows carry <c>source='remote'</c> + the connection id.
/// </summary>
public sealed class RemoteLibraryScannerTests
{
    [Fact]
    public async Task ScanAsync_recurses_and_imports_remote_rows()
    {
        using var library = new TestLibrary();
        // Two distinct PNG byte streams so the two files hash differently
        // (otherwise the second is skipped as a content duplicate).
        byte[] pngA = File.ReadAllBytes(TestImageFactory.CreatePng(
            Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N")),
            "a.png", width: 16, height: 10));
        byte[] pngB = File.ReadAllBytes(TestImageFactory.CreatePng(
            Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N")),
            "b.png", width: 20, height: 12));

        // Persist a NAS connection (id 1) + password so the scanner can resolve
        // the spec + decrypt the password via AppSettingsService.
        string settingsDir = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        var settings = new AppSettingsService(settingsDir);
        var spec = new NasConnectionSpec
        {
            Id = 0,
            Name = "test-nas",
            Protocol = NasConnectionProtocol.WebDav,
            Host = "nas.local",
            Port = 5006,
            RootPath = "/",
            UseTls = false,
        };
        int connId = settings.AddNasConnection(spec);
        settings.SetNasPassword(connId, "secret");
        settings.SaveNow();

        var connector = new StubScanConnector(spec, pngA, pngB);
        var factory = new StubScanFactory(connector);
        var inspector = new ImageInspector();
        var import = new ImportService(library.Db, inspector);

        var scanner = new RemoteLibraryScanner(import, factory, settings);

        ImportReport report = await scanner.ScanAsync(connId, progress: null, CancellationToken.None);

        Assert.Equal(2, report.AddedCount);
        Assert.Equal(0, report.SkippedCount);
        Assert.True(factory.LastConnectorDisposed);

        var rows = library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT file_path, source, nas_connection_id FROM images ORDER BY file_path;";
            var list = new List<(string Path, string Source, int NasId)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            return list;
        });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("remote", r.Source));
        Assert.All(rows, r => Assert.Equal(connId, r.NasId));
        Assert.Equal($"nas://{connId}/photos/a.png", rows[0].Path);
        Assert.Equal($"nas://{connId}/photos/b.png", rows[1].Path);

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(settingsDir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task ScanAsync_throws_when_connection_id_no_longer_exists()
    {
        using var library = new TestLibrary();
        string settingsDir = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        var settings = new AppSettingsService(settingsDir);
        var import = new ImportService(library.Db, new ImageInspector());
        var scanner = new RemoteLibraryScanner(import, new StubScanFactory(new StubScanConnector(new(), [], [])), settings);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scanner.ScanAsync(connectionId: 999, progress: null, CancellationToken.None));

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(settingsDir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class StubScanFactory : INasConnectorFactory
    {
        private readonly StubScanConnector _connector;
        public bool LastConnectorDisposed => _connector.Disposed;

        public StubScanFactory(StubScanConnector connector) => _connector = connector;

        public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
            => _connector;
    }

    /// <summary>
    /// Stub <see cref="INasConnector"/> that fakes a tiny WebDAV tree:
    /// <c>/</c> → <c>/photos</c> (dir); <c>/photos</c> → <c>a.png</c>, <c>b.png</c>.
    /// <see cref="OpenReadAsync"/> serves file-specific PNG bytes so the two
    /// files hash distinctly (a content-duplicate would be silently skipped).
    /// </summary>
    private sealed class StubScanConnector : INasConnector, IDisposable
    {
        private readonly byte[] _pngA;
        private readonly byte[] _pngB;
        private bool _disposed;

        public StubScanConnector(NasConnectionSpec spec, byte[] pngA, byte[] pngB)
        {
            Spec = spec;
            _pngA = pngA;
            _pngB = pngB;
        }

        public NasConnectionSpec Spec { get; }
        public bool Disposed => _disposed;

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken ct)
        {
            IReadOnlyList<NasFileInfo> result = remotePath switch
            {
                "/" => new[]
                {
                    new NasFileInfo { Path = "/photos", Name = "photos", IsDirectory = true, Size = 0 },
                },
                "/photos" => new[]
                {
                    new NasFileInfo { Path = "/photos/a.png", Name = "a.png", IsDirectory = false, Size = _pngA.Length },
                    new NasFileInfo { Path = "/photos/b.png", Name = "b.png", IsDirectory = false, Size = _pngB.Length },
                },
                _ => Array.Empty<NasFileInfo>(),
            };
            return Task.FromResult(result);
        }

        public Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken ct)
            => Task.FromResult(new NasFileInfo
            {
                Path = remotePath,
                Name = Path.GetFileName(remotePath),
                IsDirectory = remotePath == "/" || remotePath == "/photos",
                Size = BytesFor(remotePath).Length,
            });

        public Task<Stream> OpenReadAsync(string remotePath, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(BytesFor(remotePath), writable: false));

        private byte[] BytesFor(string remotePath) => remotePath.EndsWith("/b.png") ? _pngB : _pngA;

        public Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken ct)
            => throw new NotSupportedException();
        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct)
            => throw new NotSupportedException();
        public Task MakeDirectoryAsync(string remotePath, CancellationToken ct)
            => throw new NotSupportedException();
        public Task DeleteAsync(string remotePath, CancellationToken ct)
            => throw new NotSupportedException();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
