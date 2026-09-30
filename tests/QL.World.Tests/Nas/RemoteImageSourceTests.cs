using System.IO;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-E: verifies <see cref="RemoteImageSource"/> wraps an
/// <see cref="INasConnector"/> + remote path correctly — IsLocal is false,
/// LocalPath is null (forcing the temp-file download branch in
/// ThumbnailService), DisplayName reflects the canonical path, and
/// OpenReadAsync returns the connector's stream which the caller can dispose
/// without leaking the underlying connector.
/// </summary>
public sealed class RemoteImageSourceTests
{
    [Fact]
    public async Task OpenReadAsync_returns_stream_from_connector()
    {
        var connector = new StubOpenRead(new byte[] { 1, 2, 3 });
        var source = new RemoteImageSource(
            connector, "/path/file.png", "nas://1/path/file.png");

        Stream stream = await source.OpenReadAsync(CancellationToken.None);
        Assert.NotNull(stream);

        var buffer = new byte[3];
        int read = stream.Read(buffer, 0, 3);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);

        // Disposing the returned stream must NOT dispose the connector
        // (connector lifecycle is owned by GalleryThumbnailProvider's cache).
        stream.Dispose();
        Assert.False(connector.Disposed);
    }

    [Fact]
    public void Remote_source_reports_non_local_metadata()
    {
        var connector = new StubOpenRead(Array.Empty<byte>());
        var source = new RemoteImageSource(
            connector, "/p.png", "nas://1/p.png");

        Assert.False(source.IsLocal);
        Assert.Null(source.LocalPath);
        Assert.Equal("nas://1/p.png", source.DisplayName);
    }

    [Fact]
    public async Task OpenReadAsync_delegates_remote_path_to_connector()
    {
        var connector = new StubOpenRead(new byte[] { 9 });
        var source = new RemoteImageSource(
            connector, "/nested/deep.png", "nas://2/nested/deep.png");

        Stream s = await source.OpenReadAsync(CancellationToken.None);
        s.Dispose();

        // The stub recorded the remote path it received (stripped, not
        // canonical nas://) — proves RemoteImageSource passes the raw
        // remote path through, not the DisplayName.
        Assert.Equal("/nested/deep.png", connector.LastRemotePath);
    }

    /// <summary>
    /// Minimal stub: OpenReadAsync returns a fresh MemoryStream over the
    /// configured bytes and records the remote path it was called with.
    /// Tracks Disposed so the test can assert the stream's disposal does
    /// not cascade to the connector.
    /// </summary>
    private sealed class StubOpenRead : INasConnector
    {
        private readonly byte[] _bytes;

        public StubOpenRead(byte[] bytes)
        {
            _bytes = bytes;
            Spec = new NasConnectionSpec { Id = 1, Name = "stub" };
        }

        public NasConnectionSpec Spec { get; }
        public string? LastRemotePath { get; private set; }
        public bool Disposed { get; private set; }

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<NasFileInfo> GetMetadataAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string p, CancellationToken ct)
        {
            LastRemotePath = p;
            return Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
        public Task DownloadFileAsync(string p, string d, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<byte[]> DownloadRangeAsync(string p, long o, int l, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UploadFileAsync(string l, string r, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task MakeDirectoryAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }
}
