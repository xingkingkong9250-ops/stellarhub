using System.IO;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas.Smb;

/// <summary>
/// M7-B: contract tests for <see cref="SmbNasConnector"/>. Uses a local
/// temp directory as the UNC root (File/Directory APIs behave identically
/// against local and UNC paths) — the WNet auth handshake is bypassed by
/// passing <c>ownsConnection:false</c>, so tests run on any machine without
/// a real SMB share. M7-D integrates these into <c>INasConnectorContractTests</c>
/// as a parameterized case alongside the Stub connector.
/// </summary>
public sealed class SmbNasConnectorContractTests
{
    private static SmbNasConnector CreateConnector(string tempRoot)
    {
        var spec = new NasConnectionSpec
        {
            Id = 1,
            Name = "smb-test",
            Protocol = NasConnectionProtocol.Smb,
            Host = "localhost",
            RootPath = "/",
        };
        return new SmbNasConnector(spec, uncRoot: tempRoot, remoteName: null, ownsConnection: false);
    }

    [Fact]
    public void Spec_round_trips()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);
        Assert.Equal("smb-test", c.Spec.Name);
        Assert.Equal(NasConnectionProtocol.Smb, c.Spec.Protocol);
    }

    [Fact]
    public async Task TestConnectionAsync_returns_true_for_existing_root()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);
        Assert.True(await c.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TestConnectionAsync_throws_NetworkUnreachable_for_missing_root()
    {
        var spec = new NasConnectionSpec
        {
            Id = 2,
            Name = "missing",
            Protocol = NasConnectionProtocol.Smb,
            Host = "localhost",
            RootPath = "/",
        };
        string ghostRoot = Path.Combine(Path.GetTempPath(), "stellarhub-smb-nonexistent-" + Guid.NewGuid());
        using var c = new SmbNasConnector(spec, ghostRoot, remoteName: null, ownsConnection: false);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(NasConnectionKind.NetworkUnreachable, ex.Kind);
    }

    [Fact]
    public async Task ListFilesAsync_returns_immediate_children()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "a.jpg"), new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "sub"));
        using var c = CreateConnector(tempDir.Path);

        IReadOnlyList<NasFileInfo> list = await c.ListFilesAsync("/", CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, f => f.Name == "a.jpg" && !f.IsDirectory && f.Size == 1);
        Assert.Contains(list, f => f.Name == "sub" && f.IsDirectory);
    }

    [Fact]
    public async Task GetMetadataAsync_returns_file_info()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "photo.jpg"), new byte[1024]);
        using var c = CreateConnector(tempDir.Path);

        NasFileInfo info = await c.GetMetadataAsync("/photo.jpg", CancellationToken.None);
        Assert.False(info.IsDirectory);
        Assert.Equal("photo.jpg", info.Name);
        Assert.Equal(1024, info.Size);
    }

    [Fact]
    public async Task GetMetadataAsync_throws_NotFound_for_missing()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.GetMetadataAsync("/missing.jpg", CancellationToken.None));
        Assert.Equal(NasConnectionKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task OpenReadAsync_returns_readable_stream()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "bin"), new byte[] { 1, 2, 3, 4 });
        using var c = CreateConnector(tempDir.Path);

        await using (Stream s = await c.OpenReadAsync("/bin", CancellationToken.None))
        {
            Assert.True(s.CanRead);
            var buf = new byte[4];
            Assert.Equal(4, await s.ReadAsync(buf, CancellationToken.None));
            Assert.Equal(1, buf[0]);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_writes_destination()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "src.bin"), new byte[] { 1, 2, 3, 4 });
        string dest = Path.GetTempFileName();
        using var c = CreateConnector(tempDir.Path);
        try
        {
            await c.DownloadFileAsync("/src.bin", dest, CancellationToken.None);
            Assert.True(File.Exists(dest));
            Assert.Equal(4, new FileInfo(dest).Length);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task DownloadRangeAsync_returns_exact_length_and_offset_content()
    {
        using var tempDir = new TempDir();
        var content = new byte[16];
        for (int i = 0; i < 16; i++) content[i] = (byte)i;
        File.WriteAllBytes(Path.Combine(tempDir.Path, "src.bin"), content);
        using var c = CreateConnector(tempDir.Path);

        byte[] chunk = await c.DownloadRangeAsync("/src.bin", offset: 4, length: 8, CancellationToken.None);
        Assert.Equal(8, chunk.Length);
        Assert.Equal(4, chunk[0]);
        Assert.Equal(11, chunk[7]);
    }

    [Fact]
    public async Task DownloadRangeAsync_throws_RangeNotSatisfiable_past_eof()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "src.bin"), new byte[] { 1, 2, 3 });
        using var c = CreateConnector(tempDir.Path);

        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.DownloadRangeAsync("/src.bin", offset: 2, length: 4, CancellationToken.None));
        Assert.Equal(NasConnectionKind.RangeNotSatisfiable, ex.Kind);
    }

    [Fact]
    public async Task UploadFileAsync_creates_remote_file()
    {
        using var tempDir = new TempDir();
        string local = Path.GetTempFileName();
        File.WriteAllBytes(local, new byte[] { 9, 8, 7 });
        using var c = CreateConnector(tempDir.Path);
        try
        {
            await c.UploadFileAsync(local, "/up.bin", CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(tempDir.Path, "up.bin")));
            Assert.Equal(3, new FileInfo(Path.Combine(tempDir.Path, "up.bin")).Length);
        }
        finally { if (File.Exists(local)) File.Delete(local); }
    }

    [Fact]
    public async Task MakeDirectoryAsync_is_idempotent()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);

        await c.MakeDirectoryAsync("/new", CancellationToken.None);
        await c.MakeDirectoryAsync("/new", CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(tempDir.Path, "new")));
    }

    [Fact]
    public async Task DeleteAsync_is_idempotent_for_file()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "del.bin"), new byte[] { 1 });
        using var c = CreateConnector(tempDir.Path);

        await c.DeleteAsync("/del.bin", CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "del.bin")));
        // Second call must not throw.
        await c.DeleteAsync("/del.bin", CancellationToken.None);
    }

    [Fact]
    public async Task MoveAsync_renames_file_server_side()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "a.bin"), new byte[] { 1, 2 });
        using var c = CreateConnector(tempDir.Path);

        await c.MoveAsync("/a.bin", "/b.bin", CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "a.bin")));
        Assert.True(File.Exists(Path.Combine(tempDir.Path, "b.bin")));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = Directory.CreateTempSubdirectory("stellarhub-smb-test-").FullName;
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
