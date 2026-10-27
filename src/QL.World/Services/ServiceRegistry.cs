using System.IO;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Ai;
using QL.World.Services.Colors;
using QL.World.Services.Nas;
using QL.World.Services.Pbr;

namespace QL.World.Services;

/// <summary>
/// Aggregates all application services into a single container.
/// Eliminates long constructor parameter lists and makes dependencies explicit.
/// Hand-written constructor injection (QianLan.YinWu convention);
/// no DI container package.
/// </summary>
public sealed class ServiceRegistry
{
    public AppSettingsService Settings { get; }
    public DatabaseService Database { get; }
    public ImageInspector ImageInspector { get; }
    public ImportService Import { get; }
    public ThumbnailService Thumbnails { get; }
    public ImageQueryService ImageQuery { get; }
    public TagService Tags { get; }
    public CollectionService Collections { get; }
    public NotificationService Notifications { get; }
    public FileWatcherService Watcher { get; }
    public LegacyPythonImporter LegacyImporter { get; }
    public IAiService Ai { get; }
    public AiAnalysisService AiAnalysis { get; }
    public ColorAnalysisService Colors { get; }
    public SemanticSearchService SemanticSearch { get; }
    public PbrGroupingService PbrGrouping { get; }
    /// <summary>
    /// 0.16.0 DUP-C/D: 全库查重扫描（L1 BK-tree + L2 分块余弦）。
    /// 缩略图根目录与 <see cref="ThumbnailService"/> 保持一致，供存量 phash 补算。
    /// </summary>
    public DuplicateScanService Duplicates { get; }
    /// <summary>0.16.0 DUP-D: W8 重复审核窗口的分组读取与状态变更。</summary>
    public DuplicateReviewService DuplicateReview { get; }
    /// <summary>
    /// M5-D: builds <see cref="INasConnector"/> instances from persisted
    /// <see cref="NasConnectionSpec"/>s. Shared by <see cref="SettingsViewModel"/>
    /// (Test Connection button) and <see cref="RemoteLibraryScanner"/> (scan).
    /// </summary>
    public INasConnectorFactory NasFactory { get; }
    /// <summary>
    /// M5-F: background LRU cleaner that trims the thumbnails disk cache
    /// when it exceeds <see cref="AppSettingsService.ThumbnailCacheLimitMb"/>.
    /// </summary>
    public ThumbnailCacheService ThumbnailCache { get; }

    /// <summary>
    /// M5-G: persistent download queue (NAS→local with resume + retry).
    /// Owns the worker task; dispose at app shutdown.
    /// </summary>
    public DownloadQueueService DownloadQueue { get; }
    /// <summary>
    /// M5-H: sliding-window network health tracker. Shared by
    /// <see cref="GalleryThumbnailProvider"/> (skip remote requests when weak)
    /// and any future component that wants to gauge NAS latency.
    /// </summary>
    public NetworkConditionMonitor NetworkMonitor { get; }

    // ─── M6 多用户（登录后由 App.xaml.cs 设置）────────────────────────────

    /// <summary>M5 stub / M6 real: OS user identity (username@device).</summary>
    public IUserIdentity UserIdentity { get; }

    /// <summary>M6: currently logged-in application account; null in local-only mode.</summary>
    public CurrentUser? CurrentUser { get; set; }

    /// <summary>M6: NAS shared storage (accounts.json / library.json / pending). Null in local-only mode.</summary>
    public SharedStorageService? SharedStorage { get; set; }

    /// <summary>M6: account management service. Null in local-only mode.</summary>
    public AccountService? Accounts { get; set; }

    /// <summary>M6-D: shared library service (library.json on NAS). Null in local-only mode.</summary>
    public SharedLibraryService? SharedLibrary { get; set; }

    /// <summary>PEND-004 (0.18.0): shared collections (collections.json on the primary NAS). Null in local-only mode.</summary>
    public SharedCollectionService? SharedCollections { get; set; }

    /// <summary>M6-E: submission review service (pending/ on NAS). Null in local-only mode.</summary>
    public SubmissionService? Submissions { get; set; }

    // ─── M8 多 NAS 聚合（登录后由 App.xaml.cs 设置）─────────────────────────

    /// <summary>
    /// M8-B: 多 NAS 聚合容器，持所有 NAS 的 connector + SharedStorageService 副本，
    /// 按 <see cref="NasConnectionSpec.Id"/> 缓存。登录后由 App.xaml.cs 调
    /// <see cref="NasConnectionRegistry.EnsureAll"/> 同步。旧字段 SharedStorage/
    /// Accounts/SharedLibrary/Submissions 仍指向主 NAS 实例（向后兼容外部调用点）。
    /// Null in local-only mode.
    /// </summary>
    public NasConnectionRegistry? NasRegistry { get; set; }

    /// <summary>
    /// M8-C: 聚合视图服务。并发加载主库 + 所有副库的 library.json，注入
    /// <see cref="SharedImageRecord.SourceNasId"/>，主库优先去重。用户视角无主副之分。
    /// 旧 <see cref="SharedLibrary"/> 仍指向主库实例（兼容单库调用点）。
    /// Null in local-only mode.
    /// </summary>
    public AggregatedSharedLibraryService? AggregatedLibrary { get; set; }

    /// <summary>
    /// M8-B: 当前所有副库（Role != Primary）的 SharedStorageService 快照。
    /// 主库不包含在内。聚合视图（M8-C AggregatedSharedLibraryService）遍历此列表
    /// 并发加载每库 library。NasRegistry 未初始化（local-only）时返回空数组。
    /// 每次访问都从 NasRegistry 现算以保持与配置同步。
    /// </summary>
    public IReadOnlyList<SharedStorageService> SecondaryStorages
    {
        get
        {
            var registry = NasRegistry;
            if (registry is null) return Array.Empty<SharedStorageService>();
            return registry.GetSecondaries().Select(e => e.Storage).ToList();
        }
    }

    /// <summary>
    /// M6-F: permission checks based on the current user.
    /// </summary>
    public PermissionService Permissions { get; set; } = new(null);

    /// <summary>
    /// 0.20.0: 搜索词条日志 — 记录 chip 搜索词条（仅命中非空）。
    /// 为 null 时 TopBar 静默跳过落库（例如测试环境未注入）。
    /// </summary>
    public SearchLogService? SearchLog { get; set; }

    public ServiceRegistry(
        AppSettingsService settings,
        DatabaseService database,
        ImageInspector imageInspector,
        ImportService import,
        ThumbnailService thumbnails,
        ImageQueryService imageQuery,
        TagService tags,
        CollectionService collections,
        NotificationService notifications,
        FileWatcherService watcher,
        LegacyPythonImporter legacyImporter,
        IAiService ai,
        AiAnalysisService aiAnalysis,
        ColorAnalysisService colors,
        SemanticSearchService semanticSearch,
        PbrGroupingService pbrGrouping,
        INasConnectorFactory nasFactory,
        ThumbnailCacheService thumbnailCache,
        DownloadQueueService downloadQueue,
        NetworkConditionMonitor networkMonitor,
        IUserIdentity userIdentity)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Database = database ?? throw new ArgumentNullException(nameof(database));
        ImageInspector = imageInspector ?? throw new ArgumentNullException(nameof(imageInspector));
        Import = import ?? throw new ArgumentNullException(nameof(import));
        Thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        ImageQuery = imageQuery ?? throw new ArgumentNullException(nameof(imageQuery));
        Tags = tags ?? throw new ArgumentNullException(nameof(tags));
        Collections = collections ?? throw new ArgumentNullException(nameof(collections));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        LegacyImporter = legacyImporter ?? throw new ArgumentNullException(nameof(legacyImporter));
        Ai = ai ?? throw new ArgumentNullException(nameof(ai));
        AiAnalysis = aiAnalysis ?? throw new ArgumentNullException(nameof(aiAnalysis));
        Colors = colors ?? throw new ArgumentNullException(nameof(colors));
        SemanticSearch = semanticSearch ?? throw new ArgumentNullException(nameof(semanticSearch));
        PbrGrouping = pbrGrouping ?? throw new ArgumentNullException(nameof(pbrGrouping));
        Duplicates = new DuplicateScanService(
            database,
            thumbnailRoot: Path.Combine(
                settings.DataRoot, AppDataRootResolver.ThumbnailsFolderName),
            // 0.16.0 DUP-E: 仅云端后端具备双图 VLM 对比能力；其它后端探测为 null，L3 静默跳过。
            vlm: ai as IVlmImageCompare);
        DuplicateReview = new DuplicateReviewService(database);
        NasFactory = nasFactory ?? throw new ArgumentNullException(nameof(nasFactory));
        ThumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
        DownloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        NetworkMonitor = networkMonitor ?? throw new ArgumentNullException(nameof(networkMonitor));
        UserIdentity = userIdentity ?? throw new ArgumentNullException(nameof(userIdentity));
    }
}
