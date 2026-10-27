using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Colors;
using QL.World.Services.Nas;
using QL.World.Services.Pbr;
using QL.World.Models;
using QL.World.ViewModels;
using QL.World.Views;

using Application = System.Windows.Application;

namespace QL.World;

public partial class App : Application
{
    private const long MAX_LOG_SIZE = 1024 * 1024; // 1 MB

    private static readonly object LOG_LOCK = new();
    private static string _logDirectory = string.Empty;
    private static string LogPath => Path.Combine(_logDirectory, "error.log");

    private ShellWindow? _shellWindow;
    private ShellViewModel? _shellViewModel;
    private MaterialOrganizerWindow? _organizerWindow;
    private ServiceRegistry? _services;
    private DatabaseService? _database;
    private ThumbnailService? _thumbnails;
    private ThumbnailCacheService? _thumbnailCache;
    private FileWatcherService? _watcher;
    private DownloadQueueService? _downloadQueue;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _gracefulShutdownEvent;
    private RegisteredWaitHandle? _gracefulShutdownRegistration;

    // F-05: current title-bar (DWM) theme; flips together with the resource theme.
    private bool _isDarkTheme = true;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // F-05: apply the DWM title-bar theme to every window (existing and future).
        // Window.SourceInitialized is a CLR event (no RoutedEvent field); Loaded fires
        // after it, so the HWND already exists here.
        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyWindowLoaded));

        // 登录窗是首个显示的窗口，会被 WPF 自动设为 MainWindow；登录成功关闭它时
        // ShutdownMode=OnMainWindowClose 会连带关闭刚 Show 的 ShellWindow。
        // 启动期间改为显式关闭，主窗口显示后再恢复。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // configRoot 固定在 %APPDATA%（存 config.json + logs）；
        // dataRoot 由用户在 W7 向导中选择（db/thumbnails/models/files/分类），
        // 未配置时退回 configRoot。logs 始终在 configRoot（便于排查启动问题）。
        string configRoot = AppDataRootResolver.ResolveDefault();
        _logDirectory = Path.Combine(configRoot, AppDataRootResolver.LogsFolderName);
        EnsureConfigDirectory(configRoot);

        // ─── Single-instance guard ────────────────────────────────
        bool uiTestMode = DebugUiTestMode.IsWindowTargetingEnabledDefault();
        string mutexName = DebugUiTestMode.GetSingleInstanceMutexName(uiTestMode);
        _singleInstanceMutex = new Mutex(
            true,
            mutexName,
            out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                $"{AppInfo.ProductName} 已在运行中。", AppInfo.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        InitializeGracefulShutdownSignal(
            DebugUiTestMode.GetGracefulShutdownEventName(uiTestMode));

        DispatcherUnhandledException += (_, args) =>
        {
            WriteLog($"[UNHANDLED] {args.Exception}");
            args.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            WriteLog($"[FATAL] {ex}");
        };

        try
        {
            WriteLog("[BOOT] QL World starting.");

            var settings = new AppSettingsService(configRoot, WriteLog);
            settings.Load();

            // dataRoot = 用户配置的 DataRootPath 或退回 configRoot。
            // 首次启动未配置 → W7 向导选路径后 dataRoot 更新。
            string dataRoot = settings.DataRoot;

            // 老数据检测：configRoot 下是否已有 DB 文件（从旧版升级）。
            // 有老数据时需在 W7 前初始化 DB（迁移器需要 v9 schema）。
            bool legacyDatabasePresent = File.Exists(
                Path.Combine(configRoot, AppDataRootResolver.DatabaseFileName));

            if (legacyDatabasePresent && dataRoot == configRoot)
            {
                EnsureDataDirectories(configRoot);
                _database = new DatabaseService(configRoot);
                _database.Initialize();
            }

            if (!settings.IsLibraryRootConfigured)
            {
                var wizardViewModel = new ViewModels.FirstRunWizardViewModel(
                    settings, _database, configRoot, legacyDatabasePresent,
                    folderPicker: null, WriteLog);
                var wizard = new Views.FirstRunWizardWindow(wizardViewModel);
                if (wizard.ShowDialog() != true)
                {
                    WriteLog("[BOOT] First-run wizard cancelled; exiting.");
                    Shutdown();
                    return;
                }
                // W7 选完后 dataRoot 可能已改变。
                dataRoot = settings.DataRoot;
            }

            // 在最终 dataRoot 下创建数据目录 + 初始化 DB。
            // 若 dataRoot 与 configRoot 不同（用户选了新路径）且 DB 已在
            // configRoot 初始化过，此处在新位置重建（老 DB 仍在 configRoot）。
            EnsureDataDirectories(dataRoot);
            if (_database is null || dataRoot != configRoot)
            {
                _database = new DatabaseService(dataRoot);
                _database.Initialize();
            }

            var inspector = new ImageInspector();
            var pbrGrouping = new PbrGroupingService(_database, settings, WriteLog);
            // M11: 传入 W7 向导选定的托管库根目录（= dataRoot，统一路径）。
            var importService = new ImportService(
                _database, inspector, pbrGrouping, settings.LibraryRootPath);
            _thumbnails = new ThumbnailService(dataRoot, settings);
            _thumbnailCache = new ThumbnailCacheService(dataRoot, settings);
            _thumbnailCache.Start();
            // M11: W7「跳过」且 W3/A1 未放行时，画廊过滤未托管老行（可逆）。
            var imageQuery = new ImageQueryService(
                _database, () => settings.LegacyDataSkipped && !settings.ShowLegacyImages);
            var tagService = new TagService(_database);
            var collectionService = new CollectionService(_database);
            var notifications = new NotificationService();
            var colorAnalysis = new ColorAnalysisService(_database, WriteLog);
            var watcher = new FileWatcherService(settings, importService, imageQuery, notifications, WriteLog,
                colorAnalysis);
            // M11-F: Python 旧版库导入同样写入 CAS 托管库（库未配置时回落直存）。
            var legacyImporter = new LegacyPythonImporter(
                _database, settings.LibraryRootPath);
            IAiService aiService = AiServiceFactory.Create(settings, log: WriteLog);
            // v0.9.0 (W2/AI): backendResolver 支持导入对话框按次选择后端，
            // 不改写全局设置；解析失败时 RunBatchAsync 会发布不可用通知。
            // v0.12.0: backendResolver 签名由 AiMode 改为 backend id。
            // v0.10.0: AiLearningService 承载自适应学习 + 训练数据留存；
            // TagService 编辑事件转发为 tag_feedback 日志（只追加不删除）。
            var aiLearning = new AiLearningService(_database);
            tagService.TagAdded += (id, tag, source) =>
                aiLearning.RecordFeedback(id, tag, "added", source);
            tagService.TagRemoved += (id, tag, source) =>
                aiLearning.RecordFeedback(id, tag, "removed", source);
            var aiAnalysis = new AiAnalysisService(aiService, _database, tagService, notifications, WriteLog,
                backendId => AiServiceFactory.Create(settings, backendId, log: WriteLog),
                learning: aiLearning,
                learningEnabled: () => settings.AiLearningEnabled);
            var semanticSearch = new SemanticSearchService(_database, aiService);
            aiAnalysis.EmbeddingsUpdated += semanticSearch.Invalidate;
            _watcher = watcher;
            watcher.Start();

            // M5-G: persistent download queue (NAS→local with resume + retry).
            var userIdentity = new UserIdentityService();

            // 多协议路由工厂。WebDav + SMB + SFTP 三个子工厂均为真实实现
            // （M9-B 起 SFTP 走 SSH.NET，热切 + 主机密钥校验）。
            // DownloadQueueService 与 ServiceRegistry 共享同一无状态工厂实例。
            var nasFactory = new NasConnectorFactory(
                new WebDavConnectorFactory(),
                new SmbConnectorFactory(),
                new SftpConnectorFactory());
            var downloadQueue = new DownloadQueueService(
                _database, nasFactory, settings,
                userIdentity, imageQuery, WriteLog);
            _downloadQueue = downloadQueue;

            // M5-H: network health monitor for weak-network detection.
            var networkMonitor = new NetworkConditionMonitor();

            var services = new ServiceRegistry(
                settings,
                _database,
                inspector,
                importService,
                _thumbnails,
                imageQuery,
                tagService,
                collectionService,
                notifications,
                watcher,
                legacyImporter,
                aiService,
                aiAnalysis,
                colorAnalysis,
                semanticSearch,
                pbrGrouping,
                nasFactory,
                _thumbnailCache,
                downloadQueue,
                networkMonitor,
                userIdentity);
            _services = services;
            services.SearchLog = new SearchLogService(_database);

            // ─── M6-B: login gate ─────────────────────────────────────
            CurrentUser? currentUser = ShowLoginGate(services);
            // currentUser is null when the user chose local-only mode.
            services.CurrentUser = currentUser;

            SetTheme(settings.Theme);
            WriteLog("[STARTUP] Creating ShellViewModel...");
            _shellViewModel = new ShellViewModel(services);
            WriteLog("[STARTUP] ShellViewModel created.");
            _shellViewModel.ShowImportDialog += ShowImportDialog;
            _shellViewModel.RequestInputDialog += ShowInputDialog;
            _shellViewModel.ShowSettingsDialog += ShowSettingsDialog;
            _shellViewModel.ShowDownloadQueueDialog += ShowDownloadQueueDialog;
            _shellViewModel.ShowReviewQueueDialog += ShowReviewQueueDialog;
            _shellViewModel.ShowDuplicateReviewDialog += ShowDuplicateReviewDialog;
            _shellViewModel.ShowMaterialOrganizerDialog += ShowMaterialOrganizerDialog;
            _shellViewModel.RequestSaveCopyDialog += ShowSaveCopyDialog;
            WriteLog("[STARTUP] Creating ShellWindow...");
            _shellWindow = new ShellWindow(_shellViewModel);
            WriteLog("[STARTUP] ShellWindow created. Showing...");

            MainWindow = _shellWindow;
            _shellWindow.Show();
            // 主窗口已显示，恢复「关主窗口即退出」语义。
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            WriteLog("[STARTUP] ShellWindow shown.");
        }
        catch (Exception ex)
        {
            WriteLog($"[STARTUP] {ex}");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _gracefulShutdownRegistration?.Unregister(null);
        _gracefulShutdownRegistration = null;
        _gracefulShutdownEvent?.Dispose();
        _gracefulShutdownEvent = null;

        try
        {
            _shellViewModel?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog($"[EXIT] ViewModel cleanup failed: {ex}");
        }

        try
        {
            _thumbnails?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog($"[EXIT] Thumbnail service cleanup failed: {ex}");
        }

        try
        {
            _thumbnailCache?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog($"[EXIT] Thumbnail cache cleanup failed: {ex}");
        }

        try
        {
            _watcher?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog($"[EXIT] Watcher cleanup failed: {ex}");
        }

        try
        {
            _downloadQueue?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog($"[EXIT] Download queue cleanup failed: {ex}");
        }

        try
        {
            _database?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog($"[EXIT] Database cleanup failed: {ex}");
        }

        // M8-B: 释放 NasConnectionRegistry（含主+副所有 connector 的 HttpClient 池）。
        // 旧 SharedStorage/Accounts/SharedLibrary/Submissions 字段是主 NAS 视图别名，
        // 其 connector 由 NasRegistry 统一持有，这里一次性释放。
        try
        {
            _services?.NasRegistry?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog($"[EXIT] NAS registry cleanup failed: {ex}");
        }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // We don't own the mutex (e.g. exit due to a duplicate instance).
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowImportDialog(ImportViewModel viewModel)
    {
        if (_shellWindow is null) return;
        try
        {
            var dialog = new ImportDialog(viewModel)
            {
                Owner = _shellWindow,
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            WriteLog($"[IMPORT] Dialog failed: {ex}");
        }
    }

    private string? ShowInputDialog(string title, string prompt, string initial)
    {
        if (_shellWindow is null) return null;
        try
        {
            var dialog = new InputDialog(title, prompt, initial)
            {
                Owner = _shellWindow,
            };
            return dialog.ShowDialog() == true ? dialog.InputText : null;
        }
        catch (Exception ex)
        {
            WriteLog($"[INPUT] Dialog failed: {ex}");
            return null;
        }
    }

    /// <summary>U6-D: SaveFileDialog for 「保存副本到…」; null on cancel/failure.</summary>
    private string? ShowSaveCopyDialog(string defaultName)
    {
        if (_shellWindow is null) return null;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存共享库图片副本",
                FileName = defaultName,
                Filter = "图片文件 (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff)|"
                         + "*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|所有文件 (*.*)|*.*",
            };
            return dialog.ShowDialog(_shellWindow) == true ? dialog.FileName : null;
        }
        catch (Exception ex)
        {
            WriteLog($"[SHARED] Save-copy dialog failed: {ex}");
            return null;
        }
    }

    private void ShowSettingsDialog()
    {
        if (_shellWindow is null || _services is null || _database is null) return;
        try
        {
            var settingsViewModel = new SettingsViewModel(
                _services.Settings,
                _services.Thumbnails,
                _services.ImageQuery,
                _services.Watcher,
                _services.LegacyImporter,
                new ModelDownloadService(_services.Settings.DataRoot, WriteLog),
                _services.Notifications,
                _logDirectory,
                _services.PbrGrouping,
                _services.NasFactory,
                _services.Accounts,
                _services.CurrentUser,
                _services.NasRegistry,
                // PEND-003: 库目录在线迁移（复制 + 路径重写 + 校验 + 退出重启）。
                new LibraryRelocationService(_services.Settings, _database, WriteLog));
            settingsViewModel.ThemeChanged += theme =>
            {
                SetTheme(theme);
                WriteLog($"[SETTINGS] Theme switched to {theme}.");
            };

            bool shutdownAfterRelocation = false;
            settingsViewModel.RequestShutdownForRelocation += () =>
            {
                shutdownAfterRelocation = true;
            };

            var dialog = new SettingsWindow(settingsViewModel)
            {
                Owner = _shellWindow,
            };
            dialog.ShowDialog();

            if (shutdownAfterRelocation)
            {
                WriteLog("[SETTINGS] Library relocated; shutting down for restart.");
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            WriteLog($"[SETTINGS] Dialog failed: {ex}");
        }
    }

    private void ShowReviewQueueDialog()
    {
        if (_shellWindow is null || _services is null ||
            _services.Submissions is null ||
            _shellViewModel?.SharedThumbnails is null ||
            _services.CurrentUser is null)
        {
            return;
        }
        try
        {
            var dialog = new ReviewQueueWindow(
                _services.Submissions,
                _shellViewModel.SharedThumbnails,
                _services.CurrentUser.Username)
            {
                Owner = _shellWindow,
            };
            dialog.ShowDialog();
            // Refresh the toolbar badge after approvals/rejections.
            _ = _shellViewModel.RefreshPendingCountAsync();
        }
        catch (Exception ex)
        {
            WriteLog($"[REVIEW] Dialog failed: {ex}");
        }
    }

    private void ShowDuplicateReviewDialog()
    {
        if (_shellWindow is null || _services is null) return;
        try
        {
            var dialog = new DuplicateReviewWindow(_services)
            {
                Owner = _shellWindow,
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            WriteLog($"[DUP] Dialog failed: {ex}");
        }
    }

    /// <summary>
    /// 0.17.0 PEND-002: W9 素材组织窗口。非模态显示，便于选中条目时主窗口
    /// 画廊同步刷新；重复打开时激活已有窗口。
    /// </summary>
    private void ShowMaterialOrganizerDialog()
    {
        if (_shellWindow is null || _shellViewModel is null) return;
        try
        {
            if (_organizerWindow is { IsLoaded: true })
            {
                _organizerWindow.Activate();
                return;
            }

            var window = new MaterialOrganizerWindow(_shellViewModel)
            {
                Owner = _shellWindow,
            };
            window.Closed += (_, _) => _organizerWindow = null;
            _organizerWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteLog($"[ORG] Dialog failed: {ex}");
        }
    }

    private void ShowDownloadQueueDialog()
    {
        if (_shellWindow is null || _services is null) return;
        try
        {
            var dialog = new DownloadQueueWindow(_services)
            {
                Owner = _shellWindow,
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            WriteLog($"[DOWNLOAD] Dialog failed: {ex}");
        }
    }

    /// <summary>
    /// M6-B + v0.2.1 + v0.3.0 M8-A + v0.5.0: 启动登录窗口。系统自动取主 NAS
    /// （<see cref="NasConnectionSpec.IsPrimary"/>==<see cref="NasRole.Primary"/>）；
    /// M8-A 起 AppSettingsService.ValidateNasRoles 保证非空列表必含 1 个 Primary，
    /// 因此 FirstOrDefault(IsPrimary) 必中。首次启动检测 accounts.json 为空时
    /// 自动创建预置管理员 XingKongKong/zzh18254359250 并预填用户名。v0.5.0 起
    /// NAS 配置入口收敛到登录窗右上角齿轮（W5-6），打开 W3 设置窗口（默认
    /// A1 常规页，用户可切到 A7 NAS 连接页签）。
    /// </summary>
    private CurrentUser? ShowLoginGate(ServiceRegistry services)
    {
        // v0.2.1: 解析主 NAS + 探测 accounts.json 是否需初始化预置管理员
        // v0.3.0 M8-A: IsPrimary 现是 Role==Primary 的计算属性；ValidateNasRoles
        // 已在 Load() 中收敛，空列表时此处 primarySpec=null（用户须先去 A7 配置）
        var primarySpec = services.Settings.NasConnections.FirstOrDefault(c => c.IsPrimary)
            ?? services.Settings.NasConnections.FirstOrDefault();

        string? presetUsername = null;
        if (primarySpec is not null)
        {
            try
            {
                using var probeConnector = services.NasFactory.Create(primarySpec, plaintextPassword: null);
                var probeStorage = new SharedStorageService(probeConnector);
                var probeAccounts = new AccountService(probeStorage);
                // Task.Run 把 NAS 探测放到线程池线程，避免 UI 线程同步等待
                // 异步 WebDAV 操作时因 await 未配 ConfigureAwait(false) 而死锁。
                var existing = Task.Run(() => probeStorage.LoadAccountsAsync(CancellationToken.None))
                    .GetAwaiter().GetResult();
                if (existing.Count == 0)
                {
                    Task.Run(() => probeAccounts.InitializeAdminAsync(
                        username: "XingKongKong",
                        defaultPassword: "zzh18254359250",
                        CancellationToken.None)).GetAwaiter().GetResult();
                    WriteLog("[LOGIN] Bootstrap admin 'XingKongKong' created on primary NAS.");
                    presetUsername = "XingKongKong";
                }
            }
            catch (Exception ex)
            {
                WriteLog($"[LOGIN] Primary NAS probe failed (user will see error in login window): {ex.Message}");
            }
        }

        var loginVm = new LoginViewModel(services.Settings, services.NasFactory, presetUsername);
        loginVm.SettingsRequested += () => OpenLoginSettings(services, loginVm);
        var loginWindow = new LoginWindow(loginVm);

        bool? result = loginWindow.ShowDialog();
        if (result != true)
        {
            // v0.5.0: 登录窗不再有「仅本地模式」按钮——用户直接关窗即本地模式。
            WriteLog("[LOGIN] User closed login window; entering local-only mode.");
            return null;
        }

        var currentUser = loginWindow.AuthenticatedUser;
        if (currentUser is null)
            return null;

        // Build NAS connector + shared services for the user's NAS connection.
        // M8-B: 不再只为主 NAS 单独构造 connector——先 EnsureAll 同步全部
        // NAS（主+副），再让旧字段 SharedStorage/Accounts/SharedLibrary/Submissions
        // 引用 NasRegistry.GetPrimary() 实例（向后兼容外部调用点）。副库 connector
        // 失败仅日志，主库失败抛异常终止登录。
        try
        {
            var spec = services.Settings.NasConnections.FirstOrDefault(c => c.Id == currentUser.NasConnectionId);
            if (spec is null)
            {
                WriteLog($"[LOGIN] NAS connection {currentUser.NasConnectionId} not found.");
                return null;
            }

            var nasRegistry = new NasConnectionRegistry(services.NasFactory);
            nasRegistry.EnsureAll(services.Settings.NasConnections);

            var primaryEntry = nasRegistry.GetPrimary();
            if (primaryEntry is null)
            {
                // EnsureAll 未创建主库（配置/凭据错误）——登录链路无法继续
                nasRegistry.Dispose();
                WriteLog("[LOGIN] No primary NAS available after EnsureAll.");
                return null;
            }

            var storage = primaryEntry.Value.Storage;
            var accounts = new AccountService(storage);
            var sharedLibrary = new SharedLibraryService(storage);
            var submissions = new SubmissionService(storage, sharedLibrary,
                services.Thumbnails, services.Settings);

            services.NasRegistry = nasRegistry;
            services.AggregatedLibrary = new AggregatedSharedLibraryService(nasRegistry);
            services.SharedStorage = storage;
            services.Accounts = accounts;
            services.SharedLibrary = sharedLibrary;
            services.SharedCollections = new SharedCollectionService(storage);
            services.Submissions = submissions;
            services.Permissions = new PermissionService(currentUser);

            int secondaryCount = nasRegistry.GetSecondaries().Count;
            WriteLog($"[LOGIN] User '{currentUser.Username}' ({currentUser.Role}) logged in via NAS '{spec.Name}' " +
                     $"(primary) + {secondaryCount} secondary NAS connected.");
            return currentUser;
        }
        catch (Exception ex)
        {
            WriteLog($"[LOGIN] Failed to initialize shared services: {ex}");
            return null;
        }
    }

    /// <summary>
    /// v0.5.0: 登录窗角落齿轮（W5-6）回调——打开 W3「基础设置」界面，
    /// 默认停在 A1 常规页（需配 NAS 时用户自行切到 A7 页签）。仍以未登录
    /// 身份构造 SettingsViewModel（accounts=null / currentUser=null；A8
    /// 用户管理页签隐藏因 IsUserManagementEnabled=false，A7 NAS 页签可见
    /// 因 IsNasManagementEnabled 允许 null currentUser）。关闭后调
    /// loginVm.RefreshNasState() 让登录窗口立即生效新配置。
    /// </summary>
    private void OpenLoginSettings(ServiceRegistry services, ViewModels.LoginViewModel loginVm)
    {
        var settingsVm = new ViewModels.SettingsViewModel(
            services.Settings,
            services.Thumbnails,
            services.ImageQuery,
            services.Watcher,
            services.LegacyImporter,
            new ModelDownloadService(services.Settings.DataRoot, WriteLog),
            services.Notifications,
            _logDirectory,
            services.PbrGrouping,
            services.NasFactory,
            accounts: null,
            currentUser: null);
        var window = new Views.SettingsWindow(settingsVm)
        {
            Owner = null,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.ShowDialog();
        loginVm.RefreshNasState();
    }

    private static void EnsureConfigDirectory(string configRoot)
    {
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(
            Path.Combine(configRoot, AppDataRootResolver.LogsFolderName));
    }

    private static void EnsureDataDirectories(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(
            Path.Combine(dataRoot, AppDataRootResolver.ThumbnailsFolderName));
        Directory.CreateDirectory(
            Path.Combine(dataRoot, AppDataRootResolver.ModelsFolderName));
        Directory.CreateDirectory(
            Path.Combine(dataRoot, AppDataRootResolver.LogsFolderName));
    }

    private void InitializeGracefulShutdownSignal(string eventName)
    {
        try
        {
            _gracefulShutdownEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                eventName);
            _gracefulShutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
                _gracefulShutdownEvent,
                (_, timedOut) =>
                {
                    if (timedOut ||
                        Dispatcher.HasShutdownStarted ||
                        Dispatcher.HasShutdownFinished)
                    {
                        return;
                    }

                    try
                    {
                        Dispatcher.BeginInvoke(
                            new Action(Shutdown),
                            System.Windows.Threading.DispatcherPriority.Send);
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"[SHUTDOWN] Could not dispatch graceful shutdown: {ex}");
                    }
                },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            _gracefulShutdownRegistration?.Unregister(null);
            _gracefulShutdownRegistration = null;
            _gracefulShutdownEvent?.Dispose();
            _gracefulShutdownEvent = null;
            WriteLog($"[SHUTDOWN] Graceful shutdown signal unavailable: {ex}");
        }
    }

    /// <summary>
    /// Replaces the theme resource dictionary (always at merged index 0).
    /// M0 ships Dark only; Light.xaml arrives in M2.
    /// </summary>
    public void SetTheme(string themeName)
    {
        _isDarkTheme = !themeName.Equals("Light", StringComparison.OrdinalIgnoreCase);
        string sourcePath = _isDarkTheme ? "Themes/Dark.xaml" : "Themes/Light.xaml";

        try
        {
            var newThemeDict = new ResourceDictionary
            {
                Source = new Uri(sourcePath, UriKind.Relative)
            };

            var mergedDicts = Resources.MergedDictionaries;
            if (mergedDicts.Count > 0)
                mergedDicts[0] = newThemeDict;
            else
                mergedDicts.Add(newThemeDict);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Theme] Failed to load theme {themeName}: {ex.Message}");
        }

        // F-05: restyle the OS title bar of every currently open window.
        foreach (Window window in Windows)
            ApplyTitleBarTheme(window, _isDarkTheme);
    }

    private void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            ApplyTitleBarTheme(window, _isDarkTheme);
    }

    /// <summary>
    /// F-05: tells DWM to render the window's caption in dark (or light) mode.
    /// Attribute 20 is used on Windows 11 (build 22000+); 19 on earlier Windows 10/11 builds.
    /// Cosmetic only — failures are ignored on systems without support.
    /// </summary>
    private static void ApplyTitleBarTheme(Window window, bool dark)
    {
        try
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int attribute = Environment.OSVersion.Version.Build >= 22000 ? 20 : 19;
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch
        {
            // DWM dark title bar is best-effort.
        }
    }

    /// <summary>
    /// Appends a timestamped line to logs\error.log inside the data root.
    /// Truncates the file when it exceeds MAX_LOG_SIZE.
    /// </summary>
    internal static void WriteLog(string message)
    {
        try
        {
            lock (LOG_LOCK)
            {
                if (string.IsNullOrEmpty(_logDirectory))
                    return;

                Directory.CreateDirectory(_logDirectory);

                if (File.Exists(LogPath))
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Length > MAX_LOG_SIZE)
                    {
                        string text = File.ReadAllText(LogPath);
                        File.WriteAllText(LogPath, text[^(int)(MAX_LOG_SIZE / 2)..]);
                    }
                }

                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, entry);
            }
        }
        catch
        {
            // Last resort — logging must never crash the app.
        }
    }
}
