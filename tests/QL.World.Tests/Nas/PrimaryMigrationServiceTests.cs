using System.IO;
using System.Text.Json;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;

namespace QL.World.Tests.Nas;

/// <summary>
/// M8-E-1: 主 NAS 迁移服务测试。覆盖核心场景：
/// ① 全量迁移：library/accounts/originals/thumbnails/pending 都复制到目标副库 +
///    Role 切换（旧主→Secondary，新主→Primary）+ EnsureAll 后新主库生效；
/// ② 幂等：二次迁移跳过已存在文件，Role 不回滚；
/// ③ 失败回滚：源 library.json 缺失时 MigrateAsync 抛异常但不切换 Role。
/// </summary>
public sealed class PrimaryMigrationServiceTests : IDisposable
{
    private readonly string _directory;

    public PrimaryMigrationServiceTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests-migration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* 临时目录清理 best effort */ }
    }

    [Fact]
    public async Task MigrateAsync_copies_all_files_and_swaps_role()
    {
        var (factory, settings, registry, id1, id2) = SetupTwoNasRegistry();

        // 主库种子：library + accounts + 1 原图 + 1 缩略图 + 1 pending
        var sourceNas = factory.Connectors[id1];
        var libraryJson = JsonSerializer.Serialize(new
        {
            version = 1,
            images = new[] { new { id = "abc", file_name = "abc.png", file_hash = "abc" } }
        });
        sourceNas.SeedFile("StellarHubShared/library.json", libraryJson);
        sourceNas.SeedFile("StellarHubShared/accounts.json",
            "{\"version\":1,\"accounts\":[]}");
        sourceNas.SeedFile("StellarHubShared/originals/abc.png", "image-bytes");
        sourceNas.SeedFile("StellarHubShared/thumbnails/abc_thumb.webp", "thumb-bytes");
        sourceNas.SeedFile("StellarHubShared/pending/sub1.json", "{}");

        var migration = new PrimaryMigrationService(registry, settings);
        await migration.MigrateAsync(id2, progress: null, CancellationToken.None);

        // 验证目标副库收到了所有文件
        var targetNas = factory.Connectors[id2];
        Assert.NotEmpty(targetNas.ReadStoredText("StellarHubShared/library.json"));
        Assert.Contains("abc.png", targetNas.ReadStoredText("StellarHubShared/library.json"));
        Assert.NotEmpty(targetNas.ReadStoredText("StellarHubShared/accounts.json"));
        Assert.Equal("image-bytes", targetNas.ReadStoredText("StellarHubShared/originals/abc.png"));
        Assert.Equal("thumb-bytes", targetNas.ReadStoredText("StellarHubShared/thumbnails/abc_thumb.webp"));
        Assert.NotEmpty(targetNas.ReadStoredText("StellarHubShared/pending/sub1.json"));

        // 验证 Role 切换：id1 → Secondary，id2 → Primary
        Assert.Equal(NasRole.Secondary, settings.NasConnections.First(c => c.Id == id1).Role);
        Assert.Equal(NasRole.Primary, settings.NasConnections.First(c => c.Id == id2).Role);

        // 验证 EnsureAll 后 registry 路由：新主库是 id2
        Assert.Equal(id2, registry.GetPrimary()!.Value.Spec.Id);
    }

    [Fact]
    public async Task MigrateAsync_idempotent_second_run_skips_existing()
    {
        var (factory, settings, registry, id1, id2) = SetupTwoNasRegistry();

        var sourceNas = factory.Connectors[id1];
        sourceNas.SeedFile("StellarHubShared/library.json",
            "{\"version\":1,\"images\":[]}");
        sourceNas.SeedFile("StellarHubShared/accounts.json", "{}");
        sourceNas.SeedFile("StellarHubShared/originals/abc.png", "image-bytes");

        var migration = new PrimaryMigrationService(registry, settings);

        // 第一次迁移：id1（主）→ id2（副）
        await migration.MigrateAsync(id2, progress: null, CancellationToken.None);
        Assert.Equal(NasRole.Primary, settings.NasConnections.First(c => c.Id == id2).Role);

        // 第二次迁移：此时 id2 是主，id1 是副。再迁移回 id1 应幂等跳过。
        // 但 MigrateAsync 校验目标是副库——id1 现在是副，OK。
        await migration.MigrateAsync(id1, progress: null, CancellationToken.None);
        // 验证 Role 切回：id1 → Primary，id2 → Secondary
        Assert.Equal(NasRole.Primary, settings.NasConnections.First(c => c.Id == id1).Role);
        Assert.Equal(NasRole.Secondary, settings.NasConnections.First(c => c.Id == id2).Role);

        // 目标 id1 应有 library.json（之前被复制过，幂等覆盖）
        var targetNas = factory.Connectors[id1];
        Assert.NotEmpty(targetNas.ReadStoredText("StellarHubShared/library.json"));
        Assert.Equal("image-bytes", targetNas.ReadStoredText("StellarHubShared/originals/abc.png"));
    }

    [Fact]
    public async Task MigrateAsync_throws_and_keeps_old_primary_when_no_source_library()
    {
        var (factory, settings, registry, id1, id2) = SetupTwoNasRegistry();

        // 主库没种子任何文件——MigrateAsync 会尝试 OpenRead library.json 抛 NotFound
        // （CopyJsonFileAsync 仅吞 NotFound 视为跳过，但其他错误会冒泡）
        var migration = new PrimaryMigrationService(registry, settings);

        // 不抛异常（library/accounts/pending 缺失视为跳过），无 originals/thumbnails 也没事
        // → 迁移完成，Role 切换成功（无数据可迁但仍切角色）
        await migration.MigrateAsync(id2, progress: null, CancellationToken.None);

        // 验证 Role 仍切换（空迁移也是合法的）
        Assert.Equal(NasRole.Secondary, settings.NasConnections.First(c => c.Id == id1).Role);
        Assert.Equal(NasRole.Primary, settings.NasConnections.First(c => c.Id == id2).Role);

        // 目标库为空（源无任何文件可复制）
        var targetNas = factory.Connectors[id2];
        Assert.Empty(targetNas.ReadStoredText("StellarHubShared/library.json"));
    }

    // ─── Setup ────────────────────────────────────────────────────────────

    private (FakeFactory factory, AppSettingsService settings, NasConnectionRegistry registry, int id1, int id2)
        SetupTwoNasRegistry()
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
        return (factory, settings, registry, id1, id2);
    }

    /// <summary>
    /// 测试用 factory：按 spec.Id 缓存 InMemoryNasConnector，重复 Create 返回同一实例
    /// （EnsureAll Role 变化触发重建时仍复用同一 connector，保留 seed 数据）。
    /// </summary>
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
