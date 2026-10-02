using System.IO;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas.Sftp;

/// <summary>
/// M9-A: SFTP 专属行为测试，通过 <see cref="FakeSftpSession"/> 本地沙盒
/// 驱动 <see cref="SftpNasConnector"/>，对齐 SmbNasConnectorContractTests
/// 的边界覆盖：RangeNotSatisfiable / 幂等 Delete+Mkdir / POSIX 改名 /
/// Upload 自动建父目录 / 相对路径输出 / 子根路径 / 失败 Kind 透传。
/// </summary>
public sealed class SftpNasConnectorTests
{
    private static SftpNasConnector CreateConnector(string tempRoot, string rootPath = "/")
    {
        var spec = new NasConnectionSpec
        {
            Id = 1,
            Name = "sftp-test",
            Protocol = NasConnectionProtocol.Sftp,
            Host = "nas.local",
            Port = 22,
            RootPath = rootPath,
            Username = "u",
        };
        return new SftpNasConnector(spec, new FakeSftpSession(rootPath, tempRoot));
    }

    [Fact]
    public void Spec_round_trips()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);
        Assert.Equal("sftp-test", c.Spec.Name);
        Assert.Equal(NasConnectionProtocol.Sftp, c.Spec.Protocol);
        Assert.Equal(22, c.Spec.Port);
    }

    [Fact]
    public async Task TestConnectionAsync_returns_true_for_existing_root()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);
        Assert.True(await c.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TestConnectionAsync_throws_NotFound_for_missing_root()
    {
        string ghostRoot = Path.Combine(Path.GetTempPath(), "stellarhub-sftp-nonexistent-" + Guid.NewGuid());
        using var c = CreateConnector(ghostRoot);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(NasConnectionKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task ListFilesAsync_returns_immediate_children_with_relative_paths()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "a.jpg"), new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "sub"));
        using var c = CreateConnector(tempDir.Path);

        IReadOnlyList<NasFileInfo> list = await c.ListFilesAsync("/", CancellationToken.None);
        Assert.Equal(2, list.Count);
        NasFileInfo file = Assert.Single(list, f => f.Name == "a.jpg");
        Assert.False(file.IsDirectory);
        Assert.Equal(1, file.Size);
        Assert.Equal("/a.jpg", file.Path);
        NasFileInfo dir = Assert.Single(list, f => f.Name == "sub");
        Assert.True(dir.IsDirectory);
        Assert.Equal("/sub", dir.Path);
    }

    [Fact]
    public async Task ListFilesAsync_emits_paths_relative_to_sub_root()
    {
        using var tempDir = new TempDir();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "deeper"));
        File.WriteAllBytes(Path.Combine(tempDir.Path, "deeper", "x.png"), new byte[] { 5 });
        using var c = CreateConnector(tempDir.Path, rootPath: "/nasroot");

        IReadOnlyList<NasFileInfo> rootList = await c.ListFilesAsync("/", CancellationToken.None);
        Assert.Equal("/deeper", Assert.Single(rootList).Path);

        IReadOnlyList<NasFileInfo> deepList = await c.ListFilesAsync("/deeper", CancellationToken.None);
        Assert.Equal("/deeper/x.png", Assert.Single(deepList).Path);
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
        Assert.Equal("/photo.jpg", info.Path);
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
    public async Task OpenReadAsync_returns_readable_seekable_stream()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "bin"), new byte[] { 1, 2, 3, 4 });
        using var c = CreateConnector(tempDir.Path);

        await using (Stream s = await c.OpenReadAsync("/bin", CancellationToken.None))
        {
            Assert.True(s.CanRead);
            Assert.True(s.CanSeek);
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
    public async Task UploadFileAsync_creates_remote_file_and_creates_parent_dirs()
    {
        using var tempDir = new TempDir();
        string local = Path.GetTempFileName();
        File.WriteAllBytes(local, new byte[] { 9, 8, 7 });
        using var c = CreateConnector(tempDir.Path);
        try
        {
            // Deep target — neither parent exists; connector must mkdir both.
            await c.UploadFileAsync(local, "/sub/deep/up.bin", CancellationToken.None);
            string uploaded = Path.Combine(tempDir.Path, "sub", "deep", "up.bin");
            Assert.True(File.Exists(uploaded));
            Assert.Equal(3, new FileInfo(uploaded).Length);
        }
        finally { if (File.Exists(local)) File.Delete(local); }
    }

    [Fact]
    public async Task UploadFileAsync_overwrites_existing_remote_file()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "up.bin"), new byte[] { 1, 1, 1, 1 });
        string local = Path.GetTempFileName();
        File.WriteAllBytes(local, new byte[] { 2, 2 });
        using var c = CreateConnector(tempDir.Path);
        try
        {
            await c.UploadFileAsync(local, "/up.bin", CancellationToken.None);
            Assert.Equal(2, new FileInfo(Path.Combine(tempDir.Path, "up.bin")).Length);
        }
        finally { if (File.Exists(local)) File.Delete(local); }
    }

    [Fact]
    public async Task MakeDirectoryAsync_is_idempotent_and_creates_segments()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);

        await c.MakeDirectoryAsync("/a/b/c", CancellationToken.None);
        await c.MakeDirectoryAsync("/a/b/c", CancellationToken.None); // no throw
        Assert.True(Directory.Exists(Path.Combine(tempDir.Path, "a", "b", "c")));
    }

    [Fact]
    public async Task DeleteAsync_is_idempotent_for_file()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "del.bin"), new byte[] { 1 });
        using var c = CreateConnector(tempDir.Path);

        await c.DeleteAsync("/del.bin", CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "del.bin")));
        await c.DeleteAsync("/del.bin", CancellationToken.None); // already gone
    }

    [Fact]
    public async Task DeleteAsync_removes_empty_directory()
    {
        using var tempDir = new TempDir();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "emptydir"));
        using var c = CreateConnector(tempDir.Path);

        await c.DeleteAsync("/emptydir", CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(tempDir.Path, "emptydir")));
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

    [Fact]
    public async Task MoveAsync_posix_rename_replaces_existing_destination()
    {
        using var tempDir = new TempDir();
        File.WriteAllBytes(Path.Combine(tempDir.Path, "a.bin"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(tempDir.Path, "b.bin"), new byte[] { 0, 0, 0 });
        using var c = CreateConnector(tempDir.Path);

        await c.MoveAsync("/a.bin", "/b.bin", CancellationToken.None);
        Assert.Equal(1, new FileInfo(Path.Combine(tempDir.Path, "b.bin")).Length);
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "a.bin")));
    }

    [Fact]
    public async Task MoveAsync_throws_NotFound_for_missing_source()
    {
        using var tempDir = new TempDir();
        using var c = CreateConnector(tempDir.Path);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.MoveAsync("/ghost.bin", "/b.bin", CancellationToken.None));
        Assert.Equal(NasConnectionKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Session_failure_kind_propagates_through_connector()
    {
        using var tempDir = new TempDir();
        var spec = new NasConnectionSpec
        {
            Id = 3,
            Name = "sftp-fail",
            Protocol = NasConnectionProtocol.Sftp,
            Host = "nas.local",
            RootPath = "/",
            Username = "u",
        };
        var session = new FakeSftpSession("/", tempDir.Path)
        {
            FailKind = NasConnectionKind.AuthenticationFailed,
        };
        using var c = new SftpNasConnector(spec, session);

        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.ListFilesAsync("/", CancellationToken.None));
        Assert.Equal(NasConnectionKind.AuthenticationFailed, ex.Kind);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = Directory.CreateTempSubdirectory("stellarhub-sftp-test-").FullName;
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
