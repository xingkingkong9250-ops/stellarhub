using System.IO;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-E: verifies <see cref="ThumbnailService"/> can generate a WebP from a
/// remote (NAS) <see cref="IImageSource"/> by streaming the bytes through a
/// temp file in <c>%TEMP%\StellarHub\</c>, then deleting it.
/// </summary>
public sealed class ThumbnailServiceRemoteTests : IDisposable
{
    private readonly string _directory;
    private readonly AppSettingsService _settings;

    public ThumbnailServiceRemoteTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new AppSettingsService(_directory);
        _settings.Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Best effort. */ }
    }

    [Fact]
    public async Task Request_with_remote_source_generates_webp_and_cleans_temp()
    {
        // Arrange: a real PNG sitting "on the NAS" (stub returns its bytes
        // via OpenReadAsync — the path RemoteImageSource hands to the
        // connector is the stripped remote path, not the canonical nas://).
        string pngPath = TestImageFactory.CreatePng(
            Path.Combine(_directory, "nas"), "remote.png", 32, 24);
        byte[] pngBytes = File.ReadAllBytes(pngPath);
        var connector = new StubOpenReadConnector(
            pngBytes, new NasConnectionSpec { Id = 7, Name = "test-nas" });

        using var service = new ThumbnailService(_directory, _settings);
        string tempDir = Path.Combine(Path.GetTempPath(), "StellarHub");

        string? raised = null;
        service.ThumbnailReady += (h, level) => raised = h;

        // Act: request via RemoteImageSource (LocalPath is null → temp-file
        // download branch in ProcessJob).
        const string hash = "remote1234567890";
        var source = new RemoteImageSource(
            connector, "/textures/remote.png", "nas://7/textures/remote.png");
        service.Request(hash, source);

        // Assert: WebP was generated.
        string expected = service.GetThumbnailPath(hash);
        Assert.True(WaitFor(() => File.Exists(expected), TimeSpan.FromSeconds(10)));
        Assert.True(new FileInfo(expected).Length > 0);

        // ThumbnailReady fires after the WebP is on disk; give the worker a
        // beat to raise it before we assert.
        await Task.Delay(150);
        Assert.Equal(hash, raised);

        // Assert: the temp file under %TEMP%\StellarHub\ was deleted in the
        // finally block (no stale <hash>.*.tmp left behind).
        if (Directory.Exists(tempDir))
        {
            string[] leftovers = Directory.GetFiles(tempDir, $"{hash}.*.tmp");
            Assert.Empty(leftovers);
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    /// <summary>
    /// Stub <see cref="INasConnector"/> that returns a fixed byte array from
    /// <see cref="OpenReadAsync"/>. Every other method throws
    /// <see cref="NotSupportedException"/> — M5-E's remote thumbnail path only
    /// touches OpenReadAsync.
    /// </summary>
    private sealed class StubOpenReadConnector : INasConnector
    {
        private readonly byte[] _bytes;

        public StubOpenReadConnector(byte[] bytes, NasConnectionSpec spec)
        {
            _bytes = bytes;
            Spec = spec;
        }

        public NasConnectionSpec Spec { get; }

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<NasFileInfo> GetMetadataAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string p, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
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

        public void Dispose() { }
    }
}
