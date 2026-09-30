using System.IO;
using System.Text.Json;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels.Support;

namespace QL.World.Tests.Nas;

/// <summary>
/// M8-E-2: 管理员手动数据搬运服务测试。覆盖三大场景：
/// ① 单图搬运：源主库 1 张图 → 目标副库，目标 library AddOrUpdate + 源 library Remove，
///    目标原图/缩略图 bytes 一致；
/// ② 多图搬运：3 张图，部分源缺失缩略图（optional 跳过）—— 验证 succeeded 计数 +
///    errors 收集；
/// ③ 源与目标同 id：errors 收集"源与目标相同，跳过"，succeeded=0。
/// </summary>
public sealed class DataTransferServiceTests : IDisposable
{
    private readonly string _directory;

    public DataTransferServiceTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests-transfer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* 临时目录清理 best effort */ }
    }

    [Fact]
    public async Task TransferAsync_single_record_moves_files_and_updates_libraries()
    {
        var (factory, settings, registry, id1, id2) = await SetupTwoNasRegistryAsync();

        // 源主库种子：library + 1 原图 + 1 缩略图（路径走 SharedPaths 约定：
        // originals/<hash前2字符>/<hash>.png + thumbnails/<hash前2字符>/<hash>_thumb.webp）
        var sourceNas = factory.Connectors[id1];
        var sourceStorage = new SharedStorageService(sourceNas);
        // SourceNasPath=null 让 DataTransferService 用 SharedPaths.Original(hash, ext) 重建
        // 同源/同目标路径：originals/ha/hashabc.png + thumbnails/ha/hashabc_thumb.webp
        var record = new SharedImageRecord
        {
            Id = "hashabc",
            FileHash = "hashabc",
            FileName = "hashabc.png",
            SourceNasId = id1,
            SourceNasPath = null,
        };
        await sourceStorage.SaveLibraryAsync(new[] { record }, CancellationToken.None);
        sourceNas.SeedFile("StellarHubShared/originals/ha/hashabc.png", "image-bytes");
        sourceNas.SeedFile("StellarHubShared/thumbnails/ha/hashabc_thumb.webp", "thumb-bytes");

        // 目标副库（空）
        var transfer = new DataTransferService(registry);
        var result = await transfer.TransferAsync(
            new[] { record }, destNasId: id2, progress: null, CancellationToken.None);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);

        // 目标副库收到了文件（同路径，因 srcOriginal 与 dstOriginal 同）
        var targetNas = factory.Connectors[id2];
        Assert.Equal("image-bytes", targetNas.ReadStoredText("StellarHubShared/originals/ha/hashabc.png"));
        Assert.Equal("thumb-bytes", targetNas.ReadStoredText("StellarHubShared/thumbnails/ha/hashabc_thumb.webp"));

        // 目标 library 有 1 条，SourceNasId=destId，SourceNasPath=目标路径
        var targetStorage = new SharedStorageService(targetNas);
        var targetLib = await targetStorage.LoadLibraryAsync(CancellationToken.None);
        var moved = Assert.Single(targetLib);
        Assert.Equal("hashabc", moved.Id);
        Assert.Equal(id2, moved.SourceNasId);
        Assert.Equal("StellarHubShared/originals/ha/hashabc.png", moved.SourceNasPath);

        // 源 library 已移除该记录
        var sourceLibAfter = await sourceStorage.LoadLibraryAsync(CancellationToken.None);
        Assert.Empty(sourceLibAfter);

        // 源文件已被删除（跨主机 download→upload→delete 路径）
        Assert.Equal(string.Empty, sourceNas.ReadStoredText("StellarHubShared/originals/ha/hashabc.png"));
    }

    [Fact]
    public async Task TransferAsync_multiple_records_collects_partial_failures()
    {
        var (factory, settings, registry, id1, id2) = await SetupTwoNasRegistryAsync();

        var sourceNas = factory.Connectors[id1];
        var sourceStorage = new SharedStorageService(sourceNas);

        // 3 条记录：r1 完整（原图+缩略图），r2 缩略图缺失（optional 跳过），r3 原图缺失（搬运失败）
        // 路径走 SharedPaths 约定：FileName=hash.png 让 srcOriginal=dstOriginal
        var r1 = new SharedImageRecord
        {
            Id = "h1", FileHash = "h1", FileName = "h1.png", SourceNasId = id1,
        };
        var r2 = new SharedImageRecord
        {
            Id = "h2", FileHash = "h2", FileName = "h2.png", SourceNasId = id1,
        };
        var r3 = new SharedImageRecord
        {
            Id = "h3", FileHash = "h3", FileName = "h3.png", SourceNasId = id1,
        };
        await sourceStorage.SaveLibraryAsync(new[] { r1, r2, r3 }, CancellationToken.None);

        // 种子：r1+r2 原图，r3 原图缺失（搬运失败）；r1 缩略图，r2+r3 缩略图缺失
        sourceNas.SeedFile("StellarHubShared/originals/h1/h1.png", "img-a");
        sourceNas.SeedFile("StellarHubShared/originals/h2/h2.png", "img-b");
        sourceNas.SeedFile("StellarHubShared/thumbnails/h1/h1_thumb.webp", "thumb-a");
        // r3 原图没种 → 搬运 download 时抛 NotFound → 该条失败计入 errors

        var transfer = new DataTransferService(registry);
        var result = await transfer.TransferAsync(
            new[] { r1, r2, r3 }, destNasId: id2, progress: null, CancellationToken.None);

        // r1+r2 成功，r3 失败（原图缺失）
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
        Assert.Contains("h3.png", result.Errors[0]);

        // 目标 library 有 r1+r2，无 r3
        var targetNas = factory.Connectors[id2];
        var targetStorage = new SharedStorageService(targetNas);
        var targetLib = await targetStorage.LoadLibraryAsync(CancellationToken.None);
        Assert.Equal(2, targetLib.Count);
        Assert.DoesNotContain(targetLib, r => r.Id == "h3");

        // 源 library 仅剩 r3（r1+r2 已 Remove）
        var sourceLibAfter = await sourceStorage.LoadLibraryAsync(CancellationToken.None);
        var remaining = Assert.Single(sourceLibAfter);
        Assert.Equal("h3", remaining.Id);
    }

    [Fact]
    public async Task TransferAsync_unknown_dest_throws_not_connected()
    {
        var (factory, settings, registry, id1, id2) = await SetupTwoNasRegistryAsync();
        var transfer = new DataTransferService(registry);

        // destNasId=999 不在 registry → 抛 "未连接"
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transfer.TransferAsync(
                new[] { new SharedImageRecord { Id = "x", FileHash = "x", FileName = "x.png" } },
                destNasId: 999, progress: null, CancellationToken.None));
    }

    // ─── Setup ────────────────────────────────────────────────────────────

    private async Task<(FakeFactory factory, AppSettingsService settings, NasConnectionRegistry registry, int id1, int id2)>
        SetupTwoNasRegistryAsync()
    {
        var factory = new FakeFactory();
        var settings = new AppSettingsService(_directory);
        settings.Load();
        int id1 = settings.AddNasConnection(new NasConnectionSpec
        { Name = "main", Host = "h1", Role = NasRole.Primary });
        int id2 = settings.AddNasConnection(new NasConnectionSpec
        { Name = "sec", Host = "h2", Role = NasRole.Secondary });

        var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(settings.NasConnections);
        // 共享目录结构预建（迁移/搬运代码会调 EnsureSharedStructureAsync，但测试中也预建一次更稳）
        foreach (var kv in factory.Connectors)
            await new SharedStorageService(kv.Value).EnsureSharedStructureAsync(CancellationToken.None);
        return (factory, settings, registry, id1, id2);
    }

    private sealed class FakeFactory : INasConnectorFactory
    {
        public Dictionary<int, InMemoryNasConnector> Connectors { get; } = new();

        public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
        {
            if (!Connectors.TryGetValue(spec.Id, out var conn))
            {
                conn = new InMemoryNasConnector();
                Connectors[spec.Id] = conn;
            }
            return conn;
        }
    }
}
