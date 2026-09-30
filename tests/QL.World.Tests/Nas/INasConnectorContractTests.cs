using System.IO;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-A: contract smoke tests for <see cref="INasConnector"/>. A stub
/// implementation exercises every member of the interface so a signature
/// regression (return type change, missing member) surfaces here instead of
/// at M5-B <c>WebDavNasConnector</c> implementation time.
/// </summary>
public sealed class INasConnectorContractTests
{
    [Fact]
    public void Stub_implements_every_interface_member()
    {
        INasConnector connector = new StubNasConnector(new NasConnectionSpec
        {
            Id = 1,
            Name = "stub",
            Host = "nas.local",
            RootPath = "/",
        });

        Assert.Equal("stub", connector.Spec.Name);
        Assert.Equal("nas.local", connector.Spec.Host);
        Assert.Equal("/", connector.Spec.RootPath);
        Assert.Equal(NasConnectionProtocol.WebDav, connector.Spec.Protocol);
    }

    [Fact]
    public async Task TestConnectionAsync_returns_bool_and_propagates_exception()
    {
        INasConnector ok = new StubNasConnector(new());
        Assert.True(await ok.TestConnectionAsync(CancellationToken.None));

        INasConnector failing = new StubNasConnector(new(), failConnection: true);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => failing.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(NasConnectionKind.AuthenticationFailed, ex.Kind);
    }

    [Fact]
    public async Task ListFilesAsync_returns_NasFileInfo_readonly_list()
    {
        INasConnector connector = new StubNasConnector(new());
        IReadOnlyList<NasFileInfo> result = await connector.ListFilesAsync("/", CancellationToken.None);
        Assert.IsAssignableFrom<IReadOnlyList<NasFileInfo>>(result);
        Assert.Equal(2, result.Count);
        Assert.True(result.All(f => !string.IsNullOrEmpty(f.Path) && !string.IsNullOrEmpty(f.Name)));
    }

    [Fact]
    public async Task GetMetadataAsync_returns_single_NasFileInfo()
    {
        INasConnector connector = new StubNasConnector(new());
        NasFileInfo info = await connector.GetMetadataAsync("/photo.jpg", CancellationToken.None);
        Assert.False(info.IsDirectory);
        Assert.True(info.Size > 0);
        Assert.Equal("photo.jpg", info.Name);
    }

    [Fact]
    public async Task OpenReadAsync_returns_disposable_stream()
    {
        INasConnector connector = new StubNasConnector(new());
        await using (Stream stream = await connector.OpenReadAsync("/photo.jpg", CancellationToken.None))
        {
            Assert.NotNull(stream);
            Assert.True(stream.CanRead);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_writes_destination_bytes()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            INasConnector connector = new StubNasConnector(new());
            await connector.DownloadFileAsync("/photo.jpg", tempPath, CancellationToken.None);
            Assert.True(File.Exists(tempPath));
            Assert.True(new FileInfo(tempPath).Length > 0);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task DownloadRangeAsync_returns_byte_array_of_requested_length()
    {
        INasConnector connector = new StubNasConnector(new());
        byte[] chunk = await connector.DownloadRangeAsync("/photo.jpg", offset: 0, length: 16, CancellationToken.None);
        Assert.Equal(16, chunk.Length);
    }

    [Fact]
    public async Task UploadFileAsync_MakeDirectoryAsync_DeleteAsync_are_callable()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            INasConnector connector = new StubNasConnector(new());
            await connector.UploadFileAsync(tempPath, "/remote.bin", CancellationToken.None);
            await connector.MakeDirectoryAsync("/newdir", CancellationToken.None);
            await connector.DeleteAsync("/remote.bin", CancellationToken.None);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Stub <see cref="INasConnector"/> used only to exercise the interface
    /// contract. M5-B replaces it with <c>WebDavNasConnector</c> + a
    /// <c>StubHttpMessageHandler</c> for real HTTP-semantics tests.
    /// </summary>
    private sealed class StubNasConnector : INasConnector
    {
        private readonly bool _failConnection;

        public StubNasConnector(NasConnectionSpec spec, bool failConnection = false)
        {
            Spec = spec;
            _failConnection = failConnection;
        }

        public NasConnectionSpec Spec { get; }

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
        {
            if (_failConnection)
                throw new NasConnectionException(
                    NasConnectionKind.AuthenticationFailed, "stub auth failure");
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken cancellationToken)
        {
            IReadOnlyList<NasFileInfo> result = new[]
            {
                new NasFileInfo { Path = "/a.jpg", Name = "a.jpg", IsDirectory = false, Size = 1024 },
                new NasFileInfo { Path = "/sub", Name = "sub", IsDirectory = true, Size = 0 },
            };
            return Task.FromResult(result);
        }

        public Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken cancellationToken)
            => Task.FromResult(new NasFileInfo
            {
                Path = remotePath,
                Name = Path.GetFileName(remotePath),
                IsDirectory = false,
                Size = 1024,
            });

        public Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3, 4 }));

        public Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken cancellationToken)
        {
            File.WriteAllBytes(destinationPath, new byte[] { 1, 2, 3, 4 });
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken)
        {
            byte[] chunk = new byte[length];
            for (int i = 0; i < length; i++) chunk[i] = (byte)(i & 0xFF);
            return Task.FromResult(chunk);
        }

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task MakeDirectoryAsync(string remotePath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Dispose() { }
    }
}
