using System.IO;
using QL.World.Services.Nas;
using QL.World.Services.Nas.Smb;
using QL.World.Services.Nas.Sftp;
using QL.World.Tests.Nas.Sftp;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-A 起接口契约 smoke 测试；M7-D 参数化为多 connector 共跑。
///
/// 每个 <see cref="ConnectorCase"/> 暴露一组同名上下文（已知文件路径 /
/// 大小 / 期望失败 Kind / 期望 List 数量），让 stub 与 Smb-localTemp 走同
/// 一份断言；具体的边界行为（RangeNotSatisfiable / idempotent Delete /
/// Move cross-volume fallback）留在 <c>SmbNasConnectorContractTests</c>
/// 与 <c>WebDavNasConnectorTests</c> 各自的专属套件里，避免在参数化里
/// 把分支塞进断言。
///
/// WebDav 不参与参数化——它依赖 HttpMessageHandler 难以与本地文件 sandbox
/// 统一，保留在 <c>WebDavNasConnectorTests</c> 独立 14 项。
/// </summary>
public sealed class INasConnectorContractTests
{
    public static IEnumerable<object[]> ConnectorCases()
    {
        yield return new object[] { ConnectorCase.Stub() };
        yield return new object[] { ConnectorCase.SmbLocalTemp() };
        yield return new object[] { ConnectorCase.SftpLocalFake() };
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public void Spec_round_trips(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        {
            Assert.Equal("contract-test", c.Spec.Name);
            Assert.Equal(ctx.ExpectedProtocol, c.Spec.Protocol);
            Assert.Equal("nas.local", c.Spec.Host);
            Assert.Equal("/", c.Spec.RootPath);
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task TestConnectionAsync_returns_true_for_existing_root(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        {
            Assert.True(await c.TestConnectionAsync(CancellationToken.None));
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task TestConnectionAsync_throws_NasConnectionException_with_kind(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateFailingConnector())
        {
            var ex = await Assert.ThrowsAsync<NasConnectionException>(
                () => c.TestConnectionAsync(CancellationToken.None));
            Assert.Equal(ctx.ExpectedFailureKind, ex.Kind);
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task ListFilesAsync_returns_expected_count_with_nonempty_entries(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        {
            IReadOnlyList<NasFileInfo> list = await c.ListFilesAsync("/", CancellationToken.None);
            Assert.IsAssignableFrom<IReadOnlyList<NasFileInfo>>(list);
            Assert.Equal(ctx.ExpectedListCount, list.Count);
            Assert.True(list.All(f => !string.IsNullOrEmpty(f.Path) && !string.IsNullOrEmpty(f.Name)));
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task GetMetadataAsync_returns_file_info(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        {
            NasFileInfo info = await c.GetMetadataAsync(ctx.KnownFilePath, CancellationToken.None);
            Assert.False(info.IsDirectory);
            Assert.Equal(ctx.KnownFileName, info.Name);
            Assert.Equal(ctx.KnownFileSize, info.Size);
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task OpenReadAsync_returns_readable_stream(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        await using (Stream stream = await c.OpenReadAsync(ctx.KnownFilePath, CancellationToken.None))
        {
            Assert.NotNull(stream);
            Assert.True(stream.CanRead);
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task DownloadFileAsync_writes_destination_bytes(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        {
            string tempPath = Path.GetTempFileName();
            try
            {
                await c.DownloadFileAsync(ctx.KnownFilePath, tempPath, CancellationToken.None);
                Assert.True(File.Exists(tempPath));
                Assert.Equal(ctx.KnownFileSize, new FileInfo(tempPath).Length);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task DownloadRangeAsync_returns_byte_array_of_requested_length(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        {
            byte[] chunk = await c.DownloadRangeAsync(ctx.KnownFilePath, offset: 0, length: 16, CancellationToken.None);
            Assert.Equal(16, chunk.Length);
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public async Task UploadFileAsync_MakeDirectoryAsync_DeleteAsync_are_callable(ConnectorCase ctx)
    {
        using (ctx)
        using (var c = ctx.CreateConnector())
        {
            string localPath = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(localPath, new byte[] { 1, 2, 3 });
                await c.UploadFileAsync(localPath, "/up.bin", CancellationToken.None);
                await c.MakeDirectoryAsync("/newdir", CancellationToken.None);
                await c.DeleteAsync("/up.bin", CancellationToken.None);
            }
            finally
            {
                if (File.Exists(localPath)) File.Delete(localPath);
            }
        }
    }

    /// <summary>
    /// 单 case 上下文。封装 connector 工厂 + 已知远端文件 + 期望失败 Kind
    /// 等参数化字段，让 stub 与 smb-localTemp 走同一份断言。持有临时目录的
    /// 资源时实现 <see cref="IDisposable"/>，由测试方法的 using 块清理。
    /// </summary>
    public sealed class ConnectorCase : IDisposable
    {
        public string DisplayName { get; init; } = string.Empty;
        public NasConnectionProtocol ExpectedProtocol { get; init; }
        public Func<INasConnector> CreateConnector { get; init; } = () => throw new InvalidOperationException();
        public Func<INasConnector> CreateFailingConnector { get; init; } = () => throw new InvalidOperationException();
        public NasConnectionKind ExpectedFailureKind { get; init; }
        public string KnownFilePath { get; init; } = "/photo.jpg";
        public long KnownFileSize { get; init; } = 1024;
        public string KnownFileName { get; init; } = "photo.jpg";
        public int ExpectedListCount { get; init; } = 1;

        private readonly string? _tempRoot;
        private readonly string? _ghostRoot;

        private ConnectorCase(string? tempRoot, string? ghostRoot)
        {
            _tempRoot = tempRoot;
            _ghostRoot = ghostRoot;
        }

        public static ConnectorCase Stub()
        {
            var spec = new NasConnectionSpec
            {
                Id = 1,
                Name = "contract-test",
                Protocol = NasConnectionProtocol.WebDav,
                Host = "nas.local",
                RootPath = "/",
            };
            return new ConnectorCase(tempRoot: null, ghostRoot: null)
            {
                DisplayName = "Stub",
                ExpectedProtocol = NasConnectionProtocol.WebDav,
                CreateConnector = () => new StubNasConnector(spec),
                CreateFailingConnector = () => new StubNasConnector(spec, failConnection: true),
                ExpectedFailureKind = NasConnectionKind.AuthenticationFailed,
                KnownFilePath = "/photo.jpg",
                KnownFileSize = 1024,
                KnownFileName = "photo.jpg",
                ExpectedListCount = 2,
            };
        }

        public static ConnectorCase SmbLocalTemp()
        {
            string tempRoot = Directory.CreateTempSubdirectory("stellarhub-contract-smb-").FullName;
            // 预置已知文件 photo.jpg (1024 bytes)；ListFiles(/) 返回 1 项
            File.WriteAllBytes(Path.Combine(tempRoot, "photo.jpg"), new byte[1024]);

            string ghostRoot = Path.Combine(Path.GetTempPath(), "stellarhub-contract-ghost-" + Guid.NewGuid().ToString("N"));

            var spec = new NasConnectionSpec
            {
                Id = 2,
                Name = "contract-test",
                Protocol = NasConnectionProtocol.Smb,
                Host = "nas.local",
                RootPath = "/",
            };
            return new ConnectorCase(tempRoot, ghostRoot)
            {
                DisplayName = "Smb-localTemp",
                ExpectedProtocol = NasConnectionProtocol.Smb,
                CreateConnector = () => new SmbNasConnector(spec, uncRoot: tempRoot, remoteName: null, ownsConnection: false),
                CreateFailingConnector = () => new SmbNasConnector(spec, uncRoot: ghostRoot, remoteName: null, ownsConnection: false),
                ExpectedFailureKind = NasConnectionKind.NetworkUnreachable,
                KnownFilePath = "/photo.jpg",
                KnownFileSize = 1024,
                KnownFileName = "photo.jpg",
                ExpectedListCount = 1,
            };
        }

        public static ConnectorCase SftpLocalFake()
        {
            string tempRoot = Directory.CreateTempSubdirectory("stellarhub-contract-sftp-").FullName;
            // 预置已知文件 photo.jpg (1024 bytes)；ListFiles(/) 返回 1 项
            File.WriteAllBytes(Path.Combine(tempRoot, "photo.jpg"), new byte[1024]);

            string ghostRoot = Path.Combine(Path.GetTempPath(), "stellarhub-contract-ghost-sftp-" + Guid.NewGuid().ToString("N"));

            var spec = new NasConnectionSpec
            {
                Id = 4,
                Name = "contract-test",
                Protocol = NasConnectionProtocol.Sftp,
                Host = "nas.local",
                Port = 22,
                RootPath = "/",
                Username = "contract-user",
            };
            return new ConnectorCase(tempRoot, ghostRoot)
            {
                DisplayName = "Sftp-localFake",
                ExpectedProtocol = NasConnectionProtocol.Sftp,
                CreateConnector = () => new SftpNasConnector(spec, new FakeSftpSession("/", tempRoot)),
                CreateFailingConnector = () => new SftpNasConnector(spec, new FakeSftpSession("/", ghostRoot)),
                ExpectedFailureKind = NasConnectionKind.NotFound,
                KnownFilePath = "/photo.jpg",
                KnownFileSize = 1024,
                KnownFileName = "photo.jpg",
                ExpectedListCount = 1,
            };
        }

        public void Dispose()
        {
            if (_tempRoot is not null)
            {
                try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
            }
            if (_ghostRoot is not null)
            {
                try { if (Directory.Exists(_ghostRoot)) Directory.Delete(_ghostRoot, recursive: true); } catch { }
            }
        }

        public override string ToString() => DisplayName; // xUnit 测试名显示用
    }

    /// <summary>
    /// Stub <see cref="INasConnector"/> used only to exercise the interface
    /// contract baseline. M5-B / M7-B 各自的专属行为测试在
    /// <c>WebDavNasConnectorTests</c> / <c>SmbNasConnectorContractTests</c>
    /// 中独立维护。
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
            File.WriteAllBytes(destinationPath, new byte[1024]);
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

        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Dispose() { }
    }
}
