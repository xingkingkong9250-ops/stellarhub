using QL.World.Data;
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
        DownloadQueueService downloadQueue)
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
        NasFactory = nasFactory ?? throw new ArgumentNullException(nameof(nasFactory));
        ThumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
        DownloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
    }
}
