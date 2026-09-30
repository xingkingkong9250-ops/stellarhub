using System.IO;
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
    private ServiceRegistry? _services;
    private DatabaseService? _database;
    private ThumbnailService? _thumbnails;
    private ThumbnailCacheService? _thumbnailCache;
    private FileWatcherService? _watcher;
    private DownloadQueueService? _downloadQueue;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _gracefulShutdownEvent;
    private RegisteredWaitHandle? _gracefulShutdownRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string dataRoot = AppDataRootResolver.ResolveDefault();
        _logDirectory = Path.Combine(dataRoot, AppDataRootResolver.LogsFolderName);
        EnsureDataDirectories(dataRoot);

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

            var settings = new AppSettingsService(dataRoot, WriteLog);
            settings.Load();

            _database = new DatabaseService(dataRoot);
            _database.Initialize();

            var inspector = new ImageInspector();
            var pbrGrouping = new PbrGroupingService(_database, settings, WriteLog);
            var importService = new ImportService(_database, inspector, pbrGrouping);
            _thumbnails = new ThumbnailService(dataRoot, settings);
            _thumbnailCache = new ThumbnailCacheService(dataRoot, settings);
            _thumbnailCache.Start();
            var imageQuery = new ImageQueryService(_database);
            var tagService = new TagService(_database);
            var collectionService = new CollectionService(_database);
            var notifications = new NotificationService();
            var colorAnalysis = new ColorAnalysisService(_database, WriteLog);
            var watcher = new FileWatcherService(settings, importService, imageQuery, notifications, WriteLog,
                colorAnalysis);
            var legacyImporter = new LegacyPythonImporter(_database);
            IAiService aiService = AiServiceFactory.Create(settings, log: WriteLog);
            var aiAnalysis = new AiAnalysisService(aiService, _database, tagService, notifications, WriteLog);
            var semanticSearch = new SemanticSearchService(_database, aiService);
            aiAnalysis.EmbeddingsUpdated += semanticSearch.Invalidate;
            _watcher = watcher;
            watcher.Start();

            // M5-G: persistent download queue (NAS→local with resume + retry).
            var userIdentity = new UserIdentityService();
            var downloadQueue = new DownloadQueueService(
                _database, new WebDavConnectorFactory(), settings,
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
                new WebDavConnectorFactory(),
                _thumbnailCache,
                downloadQueue,
                networkMonitor,
                userIdentity);
            _services = services;

            // ─── M6-B: login gate ─────────────────────────────────────
            CurrentUser? currentUser = ShowLoginGate(services);
            // currentUser is null when the user chose local-only mode.
            services.CurrentUser = currentUser;

            SetTheme(settings.Theme);

            _shellViewModel = new ShellViewModel(services);
            _shellViewModel.ShowImportDialog += ShowImportDialog;
            _shellViewModel.RequestInputDialog += ShowInputDialog;
            _shellViewModel.ShowSettingsDialog += ShowSettingsDialog;
            _shellViewModel.ShowDownloadQueueDialog += ShowDownloadQueueDialog;
            _shellWindow = new ShellWindow(_shellViewModel);

            MainWindow = _shellWindow;
            _shellWindow.Show();
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

    private void ShowSettingsDialog()
    {
        if (_shellWindow is null || _services is null) return;
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
                _services.NasFactory);
            settingsViewModel.ThemeChanged += theme =>
            {
                SetTheme(theme);
                WriteLog($"[SETTINGS] Theme switched to {theme}.");
            };

            var dialog = new SettingsWindow(settingsViewModel)
            {
                Owner = _shellWindow,
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            WriteLog($"[SETTINGS] Dialog failed: {ex}");
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
    /// M6-B: shows the login window and wires multi-user services on success.
    /// Returns the authenticated CurrentUser, or null when the user chose
    /// local-only mode (shared library unavailable).
    /// </summary>
    private CurrentUser? ShowLoginGate(ServiceRegistry services)
    {
        var loginVm = new LoginViewModel(services.Settings, services.NasFactory);
        var loginWindow = new LoginWindow(loginVm);

        bool? result = loginWindow.ShowDialog();
        if (result != true)
        {
            // Local-only mode: no NAS account, shared library disabled.
            WriteLog("[LOGIN] User chose local-only mode.");
            return null;
        }

        var currentUser = loginWindow.AuthenticatedUser;
        if (currentUser is null)
            return null;

        // Build NAS connector + shared services for the selected connection.
        try
        {
            var spec = services.Settings.NasConnections.FirstOrDefault(c => c.Id == currentUser.NasConnectionId);
            if (spec is null)
            {
                WriteLog($"[LOGIN] NAS connection {currentUser.NasConnectionId} not found.");
                return null;
            }

            // Re-authenticate to get the account (the window already verified).
            // Here we just set up the services for the session.
            var connector = services.NasFactory.Create(spec, plaintextPassword: null);
            var storage = new SharedStorageService(connector);
            var accounts = new AccountService(storage);

            services.SharedStorage = storage;
            services.Accounts = accounts;

            WriteLog($"[LOGIN] User '{currentUser.Username}' ({currentUser.Role}) logged in via NAS '{spec.Name}'.");
            return currentUser;
        }
        catch (Exception ex)
        {
            WriteLog($"[LOGIN] Failed to initialize shared services: {ex}");
            return null;
        }
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
        string sourcePath = themeName.Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? "Themes/Light.xaml"
            : "Themes/Dark.xaml";

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
