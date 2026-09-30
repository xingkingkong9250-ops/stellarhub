using System.Diagnostics;
using QL.World.Models;

namespace QL.World.Services.Nas;

/// <summary>
/// M8-C: 聚合视图服务。包装 <see cref="NasConnectionRegistry"/>，并发加载
/// 主库 + 所有副库的 library.json，给每条 <see cref="SharedImageRecord"/>
/// 注入 <see cref="SharedImageRecord.SourceNasId"/>（按 spec.Id 覆盖以防
/// spec 删除后 stale），按 <see cref="SharedImageRecord.Id"/>（file hash）
/// 去重——主库永远优先，副库重复条目被丢弃。用户视角无主副之分。
///
/// 副库加载失败仅 Debug 日志并跳过，不影响其他库；所有库都失败时返回空列表。
/// 复用 <see cref="SharedStorageService.LoadLibraryAsync"/>（M6-A 已实现）。
/// </summary>
public sealed class AggregatedSharedLibraryService
{
    private readonly NasConnectionRegistry _registry;

    public AggregatedSharedLibraryService(NasConnectionRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// 并发加载所有 NAS 的 library.json，注入 SourceNasId，主库优先去重。
    /// 返回单一聚合列表，用户视角无主副之分。单个副库失败仅日志不阻断。
    /// </summary>
    public async Task<IReadOnlyList<SharedImageRecord>> LoadAllAsync(CancellationToken ct)
    {
        // 在锁外取快照，避免长时间持锁阻塞 EnsureAll 等写路径
        var entries = _registry.GetAll();
        if (entries.Count == 0) return Array.Empty<SharedImageRecord>();

        // 并发加载每库；每库失败返回空列表（不影响其他库）
        var perNas = await Task.WhenAll(entries.Select(async e =>
        {
            try
            {
                var records = await e.Storage.LoadLibraryAsync(ct).ConfigureAwait(false);
                // 注入 SourceNasId（覆盖以防旧 library.json 缺该字段或 stale）
                return records.Select(r => r with { SourceNasId = e.Spec.Id }).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AggregatedLibrary] Load failed for NAS id={e.Spec.Id} name='{e.Spec.Name}': {ex.Message}");
                return new List<SharedImageRecord>();
            }
        }).ToList()).ConfigureAwait(false);

        // 去重：主库永远优先。先收集主库的 Id，再添加副库中不重复的。
        var result = new List<SharedImageRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 先放主库（保证主库优先级）
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Spec.IsPrimary) continue;
            AppendUnique(perNas[i], result, seen);
        }

        // 再放副库（按 entries 顺序，已 id 升序）
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Spec.IsPrimary) continue;
            AppendUnique(perNas[i], result, seen);
        }

        return result;
    }

    private static void AppendUnique(
        List<SharedImageRecord> source,
        List<SharedImageRecord> destination,
        HashSet<string> seen)
    {
        foreach (var r in source)
        {
            if (seen.Add(r.Id))
                destination.Add(r);
        }
    }
}
