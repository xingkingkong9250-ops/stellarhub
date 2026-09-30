using System.IO;
using System.Text;
using System.Text.Json;
using QL.World.Models;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M8-C: 聚合视图服务测试。覆盖规划三大场景：
/// ① 主+副各 50 条聚合后 100 条；
/// ② 同 hash 在主副都有时仅保留主库版本；
/// ③ 副库加载失败不影响其他库。
/// 另覆盖 SourceNasId 注入覆盖、空库、单库（无副）兼容。
/// </summary>
public sealed class AggregatedSharedLibraryServiceTests
{
    private const string LibraryPath = "StellarHubShared/library.json";

    [Fact]
    public async Task LoadAllAsync_merges_primary_and_secondary_100_records()
    {
        // 主库 50 条（id=1..50）+ 副库 50 条（id=51..100）→ 聚合 100 条
        var primarySpec = NewSpec(1, "main", NasRole.Primary);
        var secondarySpec = NewSpec(2, "sec", NasRole.Secondary);
        var primaryRecords = Enumerable.Range(1, 50)
            .Select(i => NewRecord($"hash-{i}", $"p-{i}.png")).ToList();
        var secondaryRecords = Enumerable.Range(51, 50)
            .Select(i => NewRecord($"hash-{i}", $"s-{i}.png")).ToList();

        var factory = new FakeFactory();
        factory.SetupLibrary(primarySpec, primaryRecords);
        factory.SetupLibrary(secondarySpec, secondaryRecords);

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(new[] { primarySpec, secondarySpec });

        var agg = new AggregatedSharedLibraryService(registry);
        var result = await agg.LoadAllAsync(CancellationToken.None);

        Assert.Equal(100, result.Count);
        // 主库记录 hash-1..hash-50 → SourceNasId==1；副库 hash-51..hash-100 → SourceNasId==2
        // 注意：用整数解析而非 StartsWith 避免 hash-1 误匹配 hash-10/hash-100
        var primaryResults = result.Where(r => ParseHashSuffix(r.Id) <= 50).ToList();
        var secondaryResults = result.Where(r => ParseHashSuffix(r.Id) > 50).ToList();
        Assert.Equal(50, primaryResults.Count);
        Assert.Equal(50, secondaryResults.Count);
        Assert.All(primaryResults, r => Assert.Equal(1, r.SourceNasId));
        Assert.All(secondaryResults, r => Assert.Equal(2, r.SourceNasId));
    }

    [Fact]
    public async Task LoadAllAsync_duplicate_hash_keeps_primary_version()
    {
        // 同 hash 在主副都有 → 仅保留主库版本（SourceNasId==1）
        var dup = "dup-hash";
        var primarySpec = NewSpec(1, "main", NasRole.Primary);
        var secondarySpec = NewSpec(2, "sec", NasRole.Secondary);
        var primaryRecords = new List<SharedImageRecord>
        {
            NewRecord(dup, "primary.png"),
        };
        var secondaryRecords = new List<SharedImageRecord>
        {
            NewRecord(dup, "secondary.png"), // 同 hash
        };

        var factory = new FakeFactory();
        factory.SetupLibrary(primarySpec, primaryRecords);
        factory.SetupLibrary(secondarySpec, secondaryRecords);

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(new[] { primarySpec, secondarySpec });

        var agg = new AggregatedSharedLibraryService(registry);
        var result = await agg.LoadAllAsync(CancellationToken.None);

        var single = Assert.Single(result);
        Assert.Equal(1, single.SourceNasId); // 主库版本胜出
        Assert.Equal("primary.png", single.FileName); // 主库的 FileName
    }

    [Fact]
    public async Task LoadAllAsync_secondary_load_failure_does_not_block_primary()
    {
        var primarySpec = NewSpec(1, "main", NasRole.Primary);
        var secondarySpec = NewSpec(2, "sec-fail", NasRole.Secondary);

        var factory = new FakeFactory();
        factory.SetupLibrary(primarySpec, new[] { NewRecord("h1", "p.png") });
        factory.FailOnLoad[secondarySpec.Id] = true; // 副库加载抛异常

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(new[] { primarySpec, secondarySpec });

        var agg = new AggregatedSharedLibraryService(registry);
        var result = await agg.LoadAllAsync(CancellationToken.None);

        var single = Assert.Single(result);
        Assert.Equal("h1", single.Id);
        Assert.Equal(1, single.SourceNasId);
    }

    [Fact]
    public async Task LoadAllAsync_overwrites_stale_source_nas_id()
    {
        // 旧 library.json 的 record 已有 SourceNasId=999（stale），
        // 聚合器应按 spec.Id 覆盖为正确值
        var primarySpec = NewSpec(1, "main", NasRole.Primary);
        var staleRecord = NewRecord("h1", "p.png") with { SourceNasId = 999 };

        var factory = new FakeFactory();
        factory.SetupLibrary(primarySpec, new[] { staleRecord });

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(new[] { primarySpec });

        var agg = new AggregatedSharedLibraryService(registry);
        var result = await agg.LoadAllAsync(CancellationToken.None);

        var single = Assert.Single(result);
        Assert.Equal(1, single.SourceNasId); // 覆盖为 spec.Id=1，不是 999
    }

    [Fact]
    public async Task LoadAllAsync_empty_registry_returns_empty()
    {
        var factory = new FakeFactory();
        using var registry = new NasConnectionRegistry(factory);
        // 未 EnsureAll 任何 spec → GetAll 返回空

        var agg = new AggregatedSharedLibraryService(registry);
        var result = await agg.LoadAllAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadAllAsync_primary_only_no_secondaries()
    {
        // 单库（仅主库）场景：聚合器应等价于直接 LoadAsync
        var primarySpec = NewSpec(1, "main", NasRole.Primary);
        var records = new[] { NewRecord("h1", "a.png"), NewRecord("h2", "b.png") };

        var factory = new FakeFactory();
        factory.SetupLibrary(primarySpec, records);

        using var registry = new NasConnectionRegistry(factory);
        registry.EnsureAll(new[] { primarySpec });

        var agg = new AggregatedSharedLibraryService(registry);
        var result = await agg.LoadAllAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(1, r.SourceNasId));
    }

    // ─── 测试桩 ───────────────────────────────────────────────────────

    private static NasConnectionSpec NewSpec(int id, string name, NasRole role) =>
        new() { Id = id, Name = name, Host = $"h{id}", Role = role };

    private static SharedImageRecord NewRecord(string hash, string fileName) =>
        new() { Id = hash, FileHash = hash, FileName = fileName, SourceNasPath = $"/orig/{fileName}" };

    /// <summary>解析 "hash-{N}" 的 N 部分，用于区分主库(1..50)/副库(51..100)记录。</summary>
    private static int ParseHashSuffix(string id)
    {
        int dash = id.LastIndexOf('-');
        return dash >= 0 && int.TryParse(id[(dash + 1)..], out int n) ? n : 0;
    }

    /// <summary>
    /// 测试专用 factory：按 spec 创建 FakeConnector，每个 connector 独立持有
    /// 自己的 library.json 字节。FailOnLoad[id]=true 时该 connector 的
    /// OpenReadAsync 抛异常模拟加载失败。
    /// </summary>
    private sealed class FakeFactory : INasConnectorFactory
    {
        private readonly Dictionary<int, FakeConnector> _connectors = new();

        public Dictionary<int, bool> FailOnLoad { get; } = new();

        public void SetupLibrary(NasConnectionSpec spec, IReadOnlyList<SharedImageRecord> records)
        {
            var wrapper = new LibraryFile { Version = 1, Images = records.ToList() };
            string json = JsonSerializer.Serialize(wrapper, JsonOpts);
            var connector = new FakeConnector(spec);
            connector.SeedFile(LibraryPath, json);
            _connectors[spec.Id] = connector;
        }

        public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
        {
            // 返回预先 SetupLibrary 的 connector；若未 setup 返回空 library
            if (!_connectors.TryGetValue(spec.Id, out var c))
            {
                c = new FakeConnector(spec);
                _connectors[spec.Id] = c;
            }
            // 测试可在 Create 前后设置 FailOnLoad[id]=true
            if (FailOnLoad.TryGetValue(spec.Id, out var fail))
                c.FailLoad = fail;
            return c;
        }

        // 让测试能从 registry 取回 connector 验证状态（可选）
        public FakeConnector? GetConnector(int id) => _connectors.TryGetValue(id, out var c) ? c : null;
    }

    private sealed class FakeConnector : INasConnector
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public FakeConnector(NasConnectionSpec spec) => Spec = spec;

        public NasConnectionSpec Spec { get; }

        /// <summary>测试注入：为 true 时 OpenReadAsync 抛异常模拟加载失败。</summary>
        internal bool FailLoad { get; set; }

        public void SeedFile(string path, string content) =>
            _files[path] = Encoding.UTF8.GetBytes(content);

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<NasFileInfo> GetMetadataAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string remotePath, CancellationToken ct)
        {
            if (FailLoad)
                throw new NasConnectionException(NasConnectionKind.NetworkUnreachable, "fake load failure");
            if (_files.TryGetValue(remotePath, out var bytes))
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            throw new NasConnectionException(NasConnectionKind.NotFound, "not found");
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
        public Task MoveAsync(string s, string d, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class LibraryFile
    {
        public int Version { get; set; }
        public List<SharedImageRecord> Images { get; set; } = new();
    }
}
