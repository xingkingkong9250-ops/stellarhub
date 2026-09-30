using System.Diagnostics;
using System.IO;
using QL.World.Models;

namespace QL.World.Services.Nas;

/// <summary>
/// M8-E: 主 NAS 迁移进度报告（管理员 UI 显示）。
/// </summary>
public sealed record MigrationProgress(int TotalFiles, int DoneFiles, string CurrentPath, string Phase);

/// <summary>
/// M8-E: 主 NAS 自主迁移服务。管理员"设为主 NAS + 迁移"触发——把旧主库的
/// accounts.json / library.json / pending/*.json / originals/ / thumbnails/
/// 全部复制到目标副库，复制成功后切换 Role（旧主→Secondary，新主→Primary）。
///
/// 策略（NAS 无文件锁，无法真正跨主机事务）：
/// 1. 阶段 1（元数据）：复制 accounts/library/pending JSON（逐文件 OpenRead→Upload）
/// 2. 阶段 2（文件）：遍历 originals/+thumbnails/，download→upload fallback；
///    每文件先 GetMetadataAsync 探测目标已存在则跳过（幂等，支持断点续传）
/// 3. 阶段 3（切换）：旧主 Role=Secondary，新主 Role=Primary，落盘 AppSettingsService
/// 失败回滚：不切换 Role，抛异常让调用方提示用户；目标库已复制的文件保留
/// （幂等，下次重试跳过）。accounts.json 最后覆盖（避免登录到一半状态）。
/// </summary>
public sealed class PrimaryMigrationService
{
    private readonly NasConnectionRegistry _registry;
    private readonly AppSettingsService _settings;

    public PrimaryMigrationService(NasConnectionRegistry registry, AppSettingsService settings)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 执行迁移：把旧主库数据复制到 newPrimaryId 指定的副库，成功后切换 Role。
    /// 失败抛异常，不切换 Role（保留旧主）。幂等：重试时跳过已复制文件。
    /// </summary>
    public async Task MigrateAsync(
        int newPrimaryId,
        IProgress<MigrationProgress>? progress,
        CancellationToken ct)
    {
        // 解析源（旧主库）+ 目标（新主库）
        var sourceEntry = _registry.GetPrimary()
            ?? throw new InvalidOperationException("无当前主 NAS，无法迁移。");
        var targetEntry = _registry.GetSecondary(newPrimaryId)
            ?? throw new InvalidOperationException($"目标 NAS id={newPrimaryId} 不是副库或未连接。");

        var sourceConn = sourceEntry.Connector;
        var targetConn = targetEntry.Connector;
        var targetStorage = targetEntry.Storage;

        // 阶段 0：校验目标可写 + 建共享目录结构
        progress?.Report(new MigrationProgress(0, 0, SharedPaths.Root, "校验目标"));
        await targetConn.TestConnectionAsync(ct).ConfigureAwait(false);
        await targetStorage.EnsureSharedStructureAsync(ct).ConfigureAwait(false);

        // 阶段 1：复制元数据 JSON（accounts 最后覆盖）
        progress?.Report(new MigrationProgress(0, 0, "library/pending", "复制元数据"));
        await CopyJsonFileAsync(sourceConn, targetConn, SharedPaths.Library, ct).ConfigureAwait(false);
        await CopyPendingFilesAsync(sourceConn, targetConn, ct).ConfigureAwait(false);

        // 阶段 2：复制 originals/ + thumbnails/（幂等，跳过已存在）
        await CopyDirectoryTreeAsync(sourceConn, targetConn,
            $"{SharedPaths.Root}/{SharedPaths.OriginalsDir}", progress, ct).ConfigureAwait(false);
        await CopyDirectoryTreeAsync(sourceConn, targetConn,
            $"{SharedPaths.Root}/{SharedPaths.ThumbnailsDir}", progress, ct).ConfigureAwait(false);

        // accounts.json 最后覆盖（避免登录到一半状态）
        progress?.Report(new MigrationProgress(0, 0, SharedPaths.Accounts, "复制 accounts"));
        await CopyJsonFileAsync(sourceConn, targetConn, SharedPaths.Accounts, ct).ConfigureAwait(false);

        // 阶段 3：切换 Role（旧主→Secondary，新主→Primary）+ 落盘
        progress?.Report(new MigrationProgress(0, 0, "切换主 NAS", "切换 Role"));
        SwapPrimaryRole(sourceEntry.Spec.Id, newPrimaryId);
        // AppSettingsService.SaveNow 同步落盘 _settings 全量（含 Role 突变）
        _settings.SaveNow();

        // 阶段 4：NasRegistry 重新解析（让新主库 connector 生效）
        progress?.Report(new MigrationProgress(0, 0, "重新解析", "完成"));
        _registry.EnsureAll(_settings.NasConnections);
    }

    /// <summary>复制单个 JSON 文件（源 OpenRead → 目标 Upload）。幂等：覆盖。</summary>
    private static async Task CopyJsonFileAsync(
        INasConnector source, INasConnector target,
        string remotePath, CancellationToken ct)
    {
        // 用临时文件中转（OpenReadAsync 返回 Stream，UploadFileAsync 需要本地路径）
        string tmp = Path.GetTempFileName();
        try
        {
            await using (var src = await source.OpenReadAsync(remotePath, ct).ConfigureAwait(false))
            using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            await target.UploadFileAsync(tmp, remotePath, ct).ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            // 源文件不存在则跳过（新装主库可能没有 pending）
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>遍历 pending/ 目录复制所有 *.json（幂等覆盖）。</summary>
    private static async Task CopyPendingFilesAsync(
        INasConnector source, INasConnector target, CancellationToken ct)
    {
        var pendingDir = $"{SharedPaths.Root}/{SharedPaths.PendingDir}";
        IReadOnlyList<NasFileInfo> files;
        try
        {
            files = await source.ListFilesAsync(pendingDir, ct).ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            return; // 源无 pending 目录
        }

        foreach (var f in files.Where(f => !f.IsDirectory && f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            await CopyJsonFileAsync(source, target, $"{pendingDir}/{f.Name}", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 递归复制目录树（originals/ 或 thumbnails/）。逐文件 download→upload fallback；
    /// 幂等：目标已存在则跳过（GetMetadataAsync 探测，NotFound 视为不存在）。
    /// </summary>
    private static async Task CopyDirectoryTreeAsync(
        INasConnector source, INasConnector target,
        string dirPath, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        IReadOnlyList<NasFileInfo> entries;
        try
        {
            entries = await source.ListFilesAsync(dirPath, ct).ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            return; // 源目录不存在
        }

        // 先建目录（幂等）
        await target.MakeDirectoryAsync(dirPath, ct).ConfigureAwait(false);

        int done = 0;
        int total = entries.Count;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string childPath = $"{dirPath}/{entry.Name}";

            if (entry.IsDirectory)
            {
                await CopyDirectoryTreeAsync(source, target, childPath, progress, ct).ConfigureAwait(false);
                continue;
            }

            progress?.Report(new MigrationProgress(total, done, childPath, "复制文件"));

            // 幂等：目标已存在则跳过
            bool exists = false;
            try
            {
                await target.GetMetadataAsync(childPath, ct).ConfigureAwait(false);
                exists = true;
            }
            catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
            {
                exists = false;
            }

            if (!exists)
            {
                string tmp = Path.GetTempFileName();
                try
                {
                    await source.DownloadFileAsync(childPath, tmp, ct).ConfigureAwait(false);
                    await target.UploadFileAsync(tmp, childPath, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(tmp); } catch { /* best effort */ }
                }
            }

            done++;
            progress?.Report(new MigrationProgress(total, done, childPath, "复制文件"));
        }
    }

    /// <summary>切换 Role：旧主→Secondary，新主→Primary。AppSettingsService 落盘由调用方。</summary>
    private void SwapPrimaryRole(int oldPrimaryId, int newPrimaryId)
    {
        foreach (var c in _settings.NasConnections)
        {
            if (c.Id == oldPrimaryId)
                c.Role = NasRole.Secondary;
            else if (c.Id == newPrimaryId)
                c.Role = NasRole.Primary;
        }
    }
}
