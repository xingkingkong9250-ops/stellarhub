using System.IO;
using QL.World.Models;
using QL.World.Services.Nas;

namespace QL.World.ViewModels.Support;

/// <summary>
/// M8-E: 管理员手动数据搬运进度报告（UI 显示）。
/// </summary>
public sealed record TransferProgress(int Total, int Done, string CurrentFile, string Phase);

/// <summary>
/// M8-E: 单次搬运结果。Succeeded 为成功条数；Failed 为失败条数；
/// Errors 收集每条失败原因（"文件名: 异常消息"），便于 UI 提示。
/// </summary>
public sealed record TransferResult(int Succeeded, int Failed, IReadOnlyList<string> Errors);

/// <summary>
/// M8-E: 管理员手动数据搬运服务。从聚合画廊选 N 张共享图 → 搬运到目标副库。
///
/// 流程（逐张）：
/// 1. 解析源 NAS（按 record.SourceNasId 或回退到主库）+ 目标副库
/// 2. 搬运原图：源 connector 与目标 connector 同实例时优先 MOVE（同主机服务端 MOVE）；
///    跨主机时 download→upload→delete 源（避免半状态）
/// 3. 搬运缩略图（可选：源无缩略图不报错，原图搬运成功即认为该条成功）
/// 4. 目标库 library.json AddOrUpdate(record with SourceNasId=destId)
/// 5. 源库 library.json Remove(record.Id) — RemoveAll 内置幂等
///
/// 失败处理：单条失败记录到 Errors，继续下一条；调用方依 Succeeded/Failed 决定提示。
/// 源 NAS 文件不删除除非 upload 成功（download/upload 失败时源文件保留）。
/// </summary>
public sealed class DataTransferService
{
    private readonly NasConnectionRegistry _registry;

    public DataTransferService(NasConnectionRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// 把 <paramref name="records"/> 从各自源 NAS 搬运到 <paramref name="destNasId"/> 指定的副库。
    /// 源 NAS 由每条 record.SourceNasId 决定（null 回退到当前主库）。
    /// 目标必须是副库（Role != Primary）。返回成功/失败计数 + 错误清单。
    /// </summary>
    public async Task<TransferResult> TransferAsync(
        IReadOnlyList<SharedImageRecord> records,
        int destNasId,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return new TransferResult(0, 0, Array.Empty<string>());

        var destEntry = ResolveEntry(destNasId)
            ?? throw new InvalidOperationException($"目标 NAS id={destNasId} 未连接。");
        if (destEntry.Spec.IsPrimary)
            throw new InvalidOperationException("目标必须是副库，不能直接搬运到主库。");

        // 确保目标共享目录结构存在
        await destEntry.Storage.EnsureSharedStructureAsync(ct).ConfigureAwait(false);
        var destLib = new SharedLibraryService(destEntry.Storage);

        int succeeded = 0;
        var errors = new List<string>();
        int total = records.Count;
        int idx = 0;

        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            idx++;
            string displayName = string.IsNullOrEmpty(r.FileName) ? r.Id : r.FileName;
            progress?.Report(new TransferProgress(total, idx - 1, displayName, "搬运"));

            try
            {
                int sourceId = r.SourceNasId
                    ?? _registry.GetPrimary()?.Spec.Id
                    ?? throw new InvalidOperationException("无当前主 NAS，无法确定源。");

                if (sourceId == destNasId)
                {
                    errors.Add($"{displayName}: 源与目标相同，跳过");
                    continue;
                }

                var sourceEntry = ResolveEntry(sourceId)
                    ?? throw new InvalidOperationException($"源 NAS id={sourceId} 未连接。");
                var sourceLib = new SharedLibraryService(sourceEntry.Storage);

                string ext = Path.GetExtension(r.FileName);
                string srcOriginal = r.SourceNasPath ?? SharedPaths.Original(r.FileHash, ext);
                string dstOriginal = SharedPaths.Original(r.FileHash, ext);
                string srcThumb = SharedPaths.Thumbnail(r.FileHash);
                string dstThumb = SharedPaths.Thumbnail(r.FileHash);

                // 搬运原图（必须成功）
                await EnsureParentDirAsync(destEntry.Connector, dstOriginal, ct).ConfigureAwait(false);
                await MoveFileAsync(sourceEntry.Connector, srcOriginal,
                    destEntry.Connector, dstOriginal, ct, optional: false).ConfigureAwait(false);

                // 搐运缩略图（可选：源无则跳过）
                await EnsureParentDirAsync(destEntry.Connector, dstThumb, ct).ConfigureAwait(false);
                await MoveFileAsync(sourceEntry.Connector, srcThumb,
                    destEntry.Connector, dstThumb, ct, optional: true).ConfigureAwait(false);

                // library 更新：目标 AddOrUpdate + 源 Remove（RemoveAll 幂等）
                var moved = r with { SourceNasId = destNasId, SourceNasPath = dstOriginal };
                await destLib.AddOrUpdateAsync(moved, ct).ConfigureAwait(false);
                await sourceLib.RemoveAsync(r.Id, ct).ConfigureAwait(false);

                succeeded++;
                progress?.Report(new TransferProgress(total, idx, displayName, "完成"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{displayName}: {ex.Message}");
            }
        }

        return new TransferResult(succeeded, records.Count - succeeded, errors);
    }

    /// <summary>
    /// 搬运单个文件：源/目标同 connector 实例时优先服务端 MOVE；跨主机时
    /// download→upload→delete 源。<paramref name="optional"/>=true 时源 NotFound 视为成功
    /// （缩略图可能从未生成）。
    /// </summary>
    private static async Task MoveFileAsync(
        INasConnector source, string sourcePath,
        INasConnector dest, string destPath,
        CancellationToken ct, bool optional)
    {
        // 同 connector 实例：服务端 MOVE（同 NAS 同主机）
        if (ReferenceEquals(source, dest))
        {
            try
            {
                await source.MoveAsync(sourcePath, destPath, ct).ConfigureAwait(false);
                return;
            }
            catch (NasConnectionException) when (optional)
            {
                return; // 缩略图缺失视为成功
            }
        }

        // 跨主机：download → upload → delete 源
        string tmp = Path.GetTempFileName();
        try
        {
            try
            {
                await source.DownloadFileAsync(sourcePath, tmp, ct).ConfigureAwait(false);
            }
            catch (NasConnectionException) when (optional)
            {
                return; // 缩略图缺失
            }

            await dest.UploadFileAsync(tmp, destPath, ct).ConfigureAwait(false);

            // 上传成功后才删源（避免半状态）
            try
            {
                await source.DeleteAsync(sourcePath, ct).ConfigureAwait(false);
            }
            catch (NasConnectionException)
            {
                // 删源失败不阻塞：原图已在目标库，残留由管理员手动清理
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>确保 <paramref name="filePath"/> 的父目录在 <paramref name="conn"/> 上存在（幂等）。</summary>
    private static async Task EnsureParentDirAsync(INasConnector conn, string filePath, CancellationToken ct)
    {
        int lastSlash = filePath.LastIndexOf('/');
        if (lastSlash <= 0) return;
        string dir = filePath[..lastSlash];
        try
        {
            await conn.MakeDirectoryAsync(dir, ct).ConfigureAwait(false);
        }
        catch (NasConnectionException)
        {
            // 已存在或服务器返回 405；视为成功
        }
    }

    /// <summary>按 id 查任意条目（主+副）。未连接返回 null。</summary>
    private NasConnectionRegistry.Entry? ResolveEntry(int id)
    {
        foreach (var e in _registry.GetAll())
            if (e.Spec.Id == id) return e;
        return null;
    }
}
