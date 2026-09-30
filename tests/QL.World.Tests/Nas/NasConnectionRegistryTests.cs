using System.IO;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M8-B: 多 NAS 聚合容器测试。覆盖三大场景：
/// ① 3 NAS（1 主 2 副）启动后缓存 3 connector + 主/副路由正确；
/// ② 副库 connector 创建失败主库仍可用（不阻断）；
/// ③ Dispose 后所有 connector 释放（无 socket 泄漏）。
/// 另覆盖 fingerprint 变化触发重建、删除 spec 释放旧 connector、
/// 重复 EnsureAll 不重建（保留 HttpClient 池）。
/// </summary>
public sealed class NasConnectionRegistryTests
{
    // ─── ① 3 NAS 启动后缓存 3 connector ────────────────────────────────

    [Fact]
    public void EnsureAll_with_3_nas_caches_3_connectors_and_routes_primary_secondaries()
    {
        var factory = new FakeFactory();
        var specs = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "main", Host = "h1", Role = NasRole.Primary },
            new() { Id = 2, Name = "sec-a", Host = "h2", Role = NasRole.Secondary },
            new() { Id = 3, Name = "sec-b", Host = "h3", Role = NasRole.Secondary },
        };

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(specs);

        Assert.Equal(3, registry.Count);
        Assert.Equal(3, factory.Created.Count);

        // 主库路由
        var primary = registry.GetPrimary();
        Assert.NotNull(primary);
        Assert.Equal(1, primary!.Value.Spec.Id);
        Assert.True(primary.Value.Spec.IsPrimary);

        // 副库路由：GetSecondaries 含 2 项（按 id 升序）
        var secondaries = registry.GetSecondaries();
        Assert.Equal(2, secondaries.Count);
        Assert.Equal(2, secondaries[0].Spec.Id);
        Assert.Equal(3, secondaries[1].Spec.Id);

        // GetSecondary(id) 对副库返回条目，对主库 id 返回 null
        Assert.NotNull(registry.GetSecondary(2));
        Assert.Null(registry.GetSecondary(1));
        Assert.Null(registry.GetSecondary(999)); // 不存在
    }

    // ─── ② 副库失败主库仍可用 ──────────────────────────────────────────

    [Fact]
    public void EnsureAll_secondary_create_failure_does_not_block_primary()
    {
        var factory = new FakeFactory();
        factory.FailIds.Add(2); // id=2 的副库 Create 抛异常

        var specs = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "main", Host = "h1", Role = NasRole.Primary },
            new() { Id = 2, Name = "sec-fail", Host = "h2", Role = NasRole.Secondary },
            new() { Id = 3, Name = "sec-ok", Host = "h3", Role = NasRole.Secondary },
        };

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(specs);

        // 主库 + sec-ok 共 2 个 connector；sec-fail 跳过
        Assert.Equal(2, registry.Count);
        Assert.NotNull(registry.GetPrimary());
        Assert.Null(registry.GetSecondary(2)); // 失败的不在缓存
        Assert.NotNull(registry.GetSecondary(3)); // 其他副库仍可用
    }

    [Fact]
    public void EnsureAll_primary_create_failure_throws()
    {
        var factory = new FakeFactory();
        factory.FailIds.Add(1); // 主库 Create 抛异常

        var specs = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "main-fail", Host = "h1", Role = NasRole.Primary },
            new() { Id = 2, Name = "sec", Host = "h2", Role = NasRole.Secondary },
        };

        using var registry = new NasConnectionRegistry(factory);
        // 主库失败抛异常（登录链路无法继续）
        Assert.Throws<InvalidOperationException>(() => registry.EnsureAll(specs));
    }

    // ─── ③ Dispose 后无 socket 泄漏 ─────────────────────────────────────

    [Fact]
    public void Dispose_releases_all_cached_connectors()
    {
        var factory = new FakeFactory();
        var specs = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "main", Host = "h1", Role = NasRole.Primary },
            new() { Id = 2, Name = "sec", Host = "h2", Role = NasRole.Secondary },
        };

        var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(specs);
        Assert.Equal(2, factory.Created.Count);
        Assert.All(factory.Created, c => Assert.False(c.IsDisposed));

        registry.Dispose();

        Assert.All(factory.Created, c => Assert.True(c.IsDisposed));
    }

    // ─── ④ fingerprint 变化触发重建，旧 connector 释放 ─────────────────

    [Fact]
    public void EnsureAll_rebuilds_when_spec_fingerprint_changes()
    {
        var factory = new FakeFactory();
        var spec = new NasConnectionSpec
        {
            Id = 1, Name = "main", Host = "h1", Role = NasRole.Primary,
        };

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(new[] { spec });
        var firstConnector = factory.Created[0];
        Assert.False(firstConnector.IsDisposed);

        // Host 变化 → fingerprint 不同 → 重建
        var updatedSpec = new NasConnectionSpec
        {
            Id = spec.Id,
            Name = spec.Name,
            Host = "h2",
            Role = spec.Role,
        };
        registry.EnsureAll(new[] { updatedSpec });

        Assert.True(firstConnector.IsDisposed); // 旧 connector 已释放
        Assert.Equal(2, factory.Created.Count); // 重建了一个新的
        var secondConnector = factory.Created[1];
        Assert.False(secondConnector.IsDisposed);
        Assert.Equal("h2", registry.GetPrimary()!.Value.Spec.Host);
    }

    // ─── ⑤ 删除 spec 后旧 connector 释放 ───────────────────────────────

    [Fact]
    public void EnsureAll_disposes_connector_for_removed_spec()
    {
        var factory = new FakeFactory();
        var specs = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "main", Host = "h1", Role = NasRole.Primary },
            new() { Id = 2, Name = "sec", Host = "h2", Role = NasRole.Secondary },
        };

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(specs);
        var secondaryConnector = factory.Created.Single(c => c.Spec.Id == 2);
        Assert.False(secondaryConnector.IsDisposed);

        // 第二次 EnsureAll：去掉 id=2
        registry.EnsureAll(new[] { specs[0] });

        Assert.Equal(1, registry.Count);
        Assert.True(secondaryConnector.IsDisposed); // 被删除的副库 connector 已释放
        Assert.Null(registry.GetSecondary(2));
    }

    // ─── ⑥ 重复 EnsureAll 同 specs 不重建（保留 HttpClient 池）─────────

    [Fact]
    public void EnsureAll_idempotent_for_unchanged_specs()
    {
        var factory = new FakeFactory();
        var specs = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "main", Host = "h1", Role = NasRole.Primary },
            new() { Id = 2, Name = "sec", Host = "h2", Role = NasRole.Secondary },
        };

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(specs);
        Assert.Equal(2, factory.Created.Count);

        // 第二次同样 specs → 不重建
        registry.EnsureAll(specs);
        Assert.Equal(2, factory.Created.Count); // 仍是 2，未新增
        Assert.All(factory.Created, c => Assert.False(c.IsDisposed));
    }

    // ─── ⑦ 主↔副 Role 切换触发重建 ─────────────────────────────────────

    [Fact]
    public void EnsureAll_rebuilds_when_role_changes_primary_to_secondary()
    {
        var factory = new FakeFactory();
        var specs = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "a", Host = "h1", Role = NasRole.Primary },
            new() { Id = 2, Name = "b", Host = "h2", Role = NasRole.Secondary },
        };

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(specs);
        Assert.Equal(1, registry.GetPrimary()!.Value.Spec.Id);

        // 交换 Role：id=2 变主，id=1 变副
        var swapped = new List<NasConnectionSpec>
        {
            new() { Id = 1, Name = "a", Host = "h1", Role = NasRole.Secondary },
            new() { Id = 2, Name = "b", Host = "h2", Role = NasRole.Primary },
        };
        registry.EnsureAll(swapped);

        Assert.Equal(2, registry.GetPrimary()!.Value.Spec.Id); // 新主库是 id=2
        var secondaries = registry.GetSecondaries();
        Assert.Single(secondaries);
        Assert.Equal(1, secondaries[0].Spec.Id); // id=1 是副库
    }

    // ─── Fake 桩 ───────────────────────────────────────────────────────

    private sealed class FakeFactory : INasConnectorFactory
    {
        public List<FakeConnector> Created { get; } = new();
        public HashSet<int> FailIds { get; } = new();

        public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
        {
            if (FailIds.Contains(spec.Id))
                throw new InvalidOperationException($"fake fail for id={spec.Id}");
            var c = new FakeConnector(spec);
            Created.Add(c);
            return c;
        }
    }

    private sealed class FakeConnector : INasConnector
    {
        public FakeConnector(NasConnectionSpec spec) => Spec = spec;
        public NasConnectionSpec Spec { get; }
        public bool IsDisposed { get; private set; }

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<NasFileInfo> GetMetadataAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
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
        public Task MoveAsync(string s, string d, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }
}
