using System.Diagnostics;

namespace QL.World.Services.Nas;

/// <summary>
/// M8-B: 多 NAS 聚合的运行时容器。打破 v0.2.1 的单实例假设——
/// <see cref="ServiceRegistry.SharedStorage"/>/Accounts/SharedLibrary/Submissions
/// 之前只绑定一个 connector，登录后无法访问副库。本注册中心持
/// <see cref="INasConnector"/> + <see cref="SharedStorageService"/> 副本，
/// 按 <see cref="NasConnectionSpec.Id"/> 缓存，懒构造并复用 HttpClient 池
/// （借鉴 <c>GalleryThumbnailProvider._connectors</c> 同款模式 line 46/269-285）。
///
/// 主 NAS（<see cref="NasConnectionSpec.Role"/>==<see cref="NasRole.Primary"/>）
/// 与副库共用同一缓存表，由 <see cref="GetPrimary"/> / <see cref="GetSecondary"/>
/// 按角色路由。主库创建失败会抛异常（登录链路无法继续）；副库创建失败仅记录
/// 日志并跳过，主库与其他副库仍可用（M8-B 测试要求）。
/// </summary>
public sealed class NasConnectionRegistry : IDisposable
{
    private readonly INasConnectorFactory _factory;
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// 缓存条目：connector + 共享存储服务 + 创建时的 spec 快照
    /// （用于检测 spec 关键字段变化以决定是否重建）。
    /// </summary>
    public readonly struct Entry
    {
        public INasConnector Connector { get; init; }
        public SharedStorageService Storage { get; init; }
        public NasConnectionSpec Spec { get; init; }
    }

    public NasConnectionRegistry(INasConnectorFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// 同步缓存到 <paramref name="specs"/>：新增 id 创建 connector + Storage；
    /// 缺失 id 释放并移除；spec 关键字段变化则释放旧 connector 重建。
    /// 已存在且 fingerprint 未变的 id 跳过，保留 HttpClient 池不重建。
    /// 单个副库创建失败仅写 Debug 日志不影响其他库；主库创建失败抛异常。
    /// </summary>
    public void EnsureAll(IReadOnlyList<NasConnectionSpec> specs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var incomingIds = new HashSet<int>();
        var fingerprints = new Dictionary<int, string>();
        foreach (var spec in specs)
        {
            incomingIds.Add(spec.Id);
            fingerprints[spec.Id] = Fingerprint(spec);
        }

        // 第一阶段：锁内计算需要重建 / 已被删除的 id 列表
        List<NasConnectionSpec> toRebuild = new();
        List<int> removedIds = new();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var spec in specs)
            {
                if (!_entries.TryGetValue(spec.Id, out var existing) ||
                    Fingerprint(existing.Spec) != fingerprints[spec.Id])
                {
                    toRebuild.Add(spec);
                }
            }
            foreach (var id in _entries.Keys)
            {
                if (!incomingIds.Contains(id))
                    removedIds.Add(id);
            }
        }

        // 第二阶段：锁外执行释放 + 创建（避免长时间持锁阻塞 GetPrimary 等读路径）
        foreach (var id in removedIds)
            RemoveAndDispose(id);

        foreach (var spec in toRebuild)
        {
            RemoveAndDispose(spec.Id); // 若旧条目存在则先释放

            INasConnector connector;
            try
            {
                connector = _factory.Create(spec, plaintextPassword: null);
            }
            catch (Exception ex) when (!spec.IsPrimary)
            {
                // 副库创建失败：不阻断，主库与其他副库仍可用
                Debug.WriteLine(
                    $"[NasRegistry] Skip secondary NAS id={spec.Id} name='{spec.Name}': {ex.Message}");
                continue;
            }

            var storage = new SharedStorageService(connector);
            lock (_gate)
            {
                if (_disposed)
                {
                    try { connector.Dispose(); } catch { /* best effort */ }
                    throw new ObjectDisposedException(nameof(NasConnectionRegistry));
                }
                _entries[spec.Id] = new Entry
                {
                    Connector = connector,
                    Storage = storage,
                    Spec = spec,
                };
            }
        }
    }

    /// <summary>当前缓存中 Role==Primary 的条目；EnsureAll 前或主缺失时为 null。</summary>
    public Entry? GetPrimary()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var kv in _entries)
                if (kv.Value.Spec.IsPrimary) return kv.Value;
            return null;
        }
    }

    /// <summary>返回指定 id 的副库条目；id 不存在或 Role==Primary 时返回 null。</summary>
    public Entry? GetSecondary(int id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(id, out var e) && !e.Spec.IsPrimary)
                return e;
            return null;
        }
    }

    /// <summary>所有副库条目（Role != Primary）的快照，按 id 升序。</summary>
    public IReadOnlyList<Entry> GetSecondaries()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries
                .Where(kv => !kv.Value.Spec.IsPrimary)
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToList();
        }
    }

    /// <summary>所有缓存条目（含主+副）的快照，按 id 升序。</summary>
    public IReadOnlyList<Entry> GetAll()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToList();
        }
    }

    /// <summary>当前缓存的条目数（含主+副）。</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<Entry> snapshot;
        lock (_gate)
        {
            snapshot = _entries.Values.ToList();
            _entries.Clear();
        }

        foreach (var entry in snapshot)
        {
            try { entry.Connector.Dispose(); }
            catch { /* best effort：释放期不抛 */ }
        }
    }

    private void RemoveAndDispose(int id)
    {
        Entry entry;
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(id, out entry);
        }
        if (!removed) return;
        try { entry.Connector.Dispose(); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// spec 的连接指纹：仅含影响 connector 构造的字段。Role 变化
    /// （主↔副切换）也触发重建，因为 <see cref="GetPrimary"/> / <see cref="GetSecondaries"/>
    /// 按 Role 路由。Name/CreatedAt 等元数据变化不触发重建。
    /// </summary>
    private static string Fingerprint(NasConnectionSpec s) =>
        string.Join('|',
            s.Id.ToString(),
            s.Protocol.ToString(),
            s.Host ?? string.Empty,
            s.Port?.ToString() ?? string.Empty,
            s.RootPath ?? string.Empty,
            s.Username ?? string.Empty,
            s.EncryptedPassword ?? string.Empty,
            s.UseTls ? "1" : "0",
            s.TrustSelfSigned ? "1" : "0",
            ((int)s.Role).ToString());
}
