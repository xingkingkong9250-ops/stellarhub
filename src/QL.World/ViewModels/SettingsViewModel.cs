using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;

namespace QL.World.ViewModels;

/// <summary>
/// W3 settings window. A1 general (theme / integrity), A2 thumbnails,
/// A4 watcher directories, A5 legacy import & diagnostics. A3 (AI) is an
/// M3 placeholder. Every change saves immediately through AppSettingsService.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly ThumbnailService _thumbnails;
    private readonly ImageQueryService _imageQuery;
    private readonly FileWatcherService _watcher;
    private readonly LegacyPythonImporter _legacyImporter;
    private readonly NotificationService _notifications;
    private readonly string _logsDirectory;

    private string _selectedTheme;
    private string? _selectedWatcherDirectory;
    private string _statisticsText = string.Empty;
    private string _legacyStatusText = string.Empty;
    private string _statusBarText = string.Empty;
    private string _legacyDbPath;
    private bool _isLegacyImporting;
    private bool _isTestingConnection;

    public SettingsViewModel(
        AppSettingsService settings,
        ThumbnailService thumbnails,
        ImageQueryService imageQuery,
        FileWatcherService watcher,
        LegacyPythonImporter legacyImporter,
        NotificationService notifications,
        string logsDirectory)
    {
        _settings = settings;
        _thumbnails = thumbnails;
        _imageQuery = imageQuery;
        _watcher = watcher;
        _legacyImporter = legacyImporter;
        _notifications = notifications;
        _logsDirectory = logsDirectory;

        _selectedTheme = settings.Theme;
        _legacyDbPath = LegacyPythonImporter.DefaultLegacyDbPath;

        foreach (string directory in settings.WatcherDirectories)
            WatcherDirectories.Add(directory);

        RegenerateAllThumbnailsCommand = new RelayCommand(
            RegenerateAllThumbnails);
        AddWatcherDirectoryCommand = new RelayCommand(AddWatcherDirectory);
        RemoveWatcherDirectoryCommand = new RelayCommand(
            RemoveWatcherDirectory,
            () => !string.IsNullOrWhiteSpace(_selectedWatcherDirectory));
        RunLegacyImportCommand = new AsyncRelayCommand(
            RunLegacyImportAsync,
            () => !_isLegacyImporting);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        TestCloudConnectionCommand = new AsyncRelayCommand(
            TestCloudConnectionAsync,
            () => IsCloudConfigured && !_isTestingConnection);

        RefreshStatistics();
    }

    // ─── A1 常规 ────────────────────────────────────────────────────────────

    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light"];

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            _settings.Theme = value;
            ThemeChanged?.Invoke(value);
        }
    }

    public bool CheckIntegrityOnStartup
    {
        get => _settings.CheckIntegrityOnStartup;
        set => _settings.CheckIntegrityOnStartup = value;
    }

    // ─── A2 缩略图 ──────────────────────────────────────────────────────────

    public int ThumbnailSize
    {
        get => _settings.ThumbnailSize;
        set => _settings.ThumbnailSize = value;
    }

    public int ThumbnailQuality
    {
        get => _settings.ThumbnailQuality;
        set => _settings.ThumbnailQuality = value;
    }

    /// <summary>Transient action feedback shown near the A2 buttons.</summary>
    public string StatusBarText
    {
        get => _statusBarText;
        private set => SetProperty(ref _statusBarText, value);
    }

    public RelayCommand RegenerateAllThumbnailsCommand { get; }

    // ─── A3 AI ──────────────────────────────────────────────────────────────

    public IReadOnlyList<AiMode> AiModeOptions { get; } =
        [AiMode.Disabled, AiMode.Local, AiMode.Cloud];

    public AiMode SelectedAiMode
    {
        get => _settings.AiMode;
        set
        {
            if (_settings.AiMode == value) return;
            _settings.AiMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCloudSectionEnabled));
            OnPropertyChanged(nameof(IsLocalSectionEnabled));
            OnPropertyChanged(nameof(AiStatusText));
        }
    }

    public bool IsCloudSectionEnabled => _settings.AiMode == AiMode.Cloud;
    public bool IsLocalSectionEnabled => _settings.AiMode == AiMode.Local;

    public string CloudBaseUrl
    {
        get => _settings.CloudBaseUrl;
        set
        {
            _settings.CloudBaseUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCloudConfigured));
            OnPropertyChanged(nameof(AiStatusText));
            TestCloudConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    public string CloudModelName
    {
        get => _settings.CloudModelName;
        set
        {
            _settings.CloudModelName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCloudConfigured));
            OnPropertyChanged(nameof(AiStatusText));
            TestCloudConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    public string CloudEmbeddingModelName
    {
        get => _settings.CloudEmbeddingModelName;
        set => _settings.CloudEmbeddingModelName = value;
    }

    /// <summary>True when base URL, model name and API key are all present.</summary>
    public bool IsCloudConfigured =>
        !string.IsNullOrWhiteSpace(_settings.CloudBaseUrl) &&
        !string.IsNullOrWhiteSpace(_settings.CloudModelName) &&
        _settings.HasCloudApiKey;

    /// <summary>
    /// Called by the view's PasswordBox on PasswordChanged. The plaintext
    /// is encrypted immediately and never stored in the ViewModel.
    /// </summary>
    public void SetCloudApiKeyInput(string plaintext)
    {
        _settings.SetCloudApiKey(plaintext);
        OnPropertyChanged(nameof(HasCloudApiKey));
        OnPropertyChanged(nameof(IsCloudConfigured));
        TestCloudConnectionCommand.RaiseCanExecuteChanged();
    }

    public bool HasCloudApiKey => _settings.HasCloudApiKey;

    public string AiStatusText
    {
        get
        {
            return _settings.AiMode switch
            {
                AiMode.Cloud => IsCloudConfigured
                    ? $"AI：云端（{_settings.CloudModelName}）"
                    : "AI：云端（未完成配置）",
                AiMode.Local => "AI：本地（M3-D 提供模型下载）",
                _ => "AI：关闭",
            };
        }
    }

    public AsyncRelayCommand TestCloudConnectionCommand { get; }

    private async Task TestCloudConnectionAsync()
    {
        if (_isTestingConnection) return;
        if (!IsCloudConfigured) return;

        _isTestingConnection = true;
        TestCloudConnectionCommand.RaiseCanExecuteChanged();
        LegacyStatusText = "正在测试连接…";

        try
        {
            var config = new CloudAiConfig
            {
                BaseUrl = CloudBaseUrl,
                ModelName = CloudModelName,
                EncryptedApiKey = _settings.CloudEncryptedApiKey,
                EmbeddingModelName = CloudEmbeddingModelName,
            };
            var cloud = new CloudAiService(config);
            bool ok = await cloud.TestConnectionAsync(CancellationToken.None);
            LegacyStatusText = ok ? "连接成功。" : "连接失败（请检查 URL、模型名与 API Key）。";
        }
        catch (Exception ex)
        {
            LegacyStatusText = $"连接失败：{ex.Message}";
        }
        finally
        {
            _isTestingConnection = false;
            TestCloudConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    // ─── A4 监控目录 ────────────────────────────────────────────────────────

    public ObservableCollection<string> WatcherDirectories { get; } = [];

    public string? SelectedWatcherDirectory
    {
        get => _selectedWatcherDirectory;
        set => SetProperty(ref _selectedWatcherDirectory, value);
    }

    public bool AutoImportOnWatch
    {
        get => _settings.AutoImportOnWatch;
        set => _settings.AutoImportOnWatch = value;
    }

    public RelayCommand AddWatcherDirectoryCommand { get; }
    public RelayCommand RemoveWatcherDirectoryCommand { get; }

    // ─── A5 关于 / 旧版导入 / 诊断 ─────────────────────────────────────────

    public string VersionText => $"v{AppInfo.Version}";

    public string StatisticsText
    {
        get => _statisticsText;
        private set => SetProperty(ref _statisticsText, value);
    }

    public string LegacyDbPath
    {
        get => _legacyDbPath;
        set => SetProperty(ref _legacyDbPath, value);
    }

    public string LegacyStatusText
    {
        get => _legacyStatusText;
        private set => SetProperty(ref _legacyStatusText, value);
    }

    public bool IsLegacyImporting
    {
        get => _isLegacyImporting;
        private set
        {
            if (SetProperty(ref _isLegacyImporting, value))
                RunLegacyImportCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand RunLegacyImportCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand CloseCommand { get; }

    // ─── Events ─────────────────────────────────────────────────────────────

    /// <summary>App wires this to SetTheme + persist (setter already persists).</summary>
    public event Action<string>? ThemeChanged;

    /// <summary>Window closes on invoke.</summary>
    public event Action? RequestClose;

    // ─── Actions ────────────────────────────────────────────────────────────

    private void RegenerateAllThumbnails()
    {
        var items = _imageQuery.QueryGallery(new LibraryFilter());
        foreach (GalleryListItem item in items)
            _thumbnails.Regenerate(item.FileHash, item.FilePath);
        StatusBarText = $"已重新排队 {items.Count} 张缩略图";
    }

    private void AddWatcherDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要监控的文件夹",
        };
        if (dialog.ShowDialog() != true) return;

        string folder = dialog.FolderName;
        string normalized = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (WatcherDirectories.Contains(normalized)) return;

        WatcherDirectories.Add(normalized);
        PersistWatcherDirectories();
    }

    private void RemoveWatcherDirectory()
    {
        if (string.IsNullOrWhiteSpace(_selectedWatcherDirectory)) return;
        WatcherDirectories.Remove(_selectedWatcherDirectory);
        PersistWatcherDirectories();
    }

    private void PersistWatcherDirectories()
    {
        _settings.SetWatcherDirectories(WatcherDirectories.ToList());
        _watcher.UpdateDirectories(_settings.WatcherDirectories);
    }

    private async Task RunLegacyImportAsync()
    {
        if (IsLegacyImporting) return;
        if (!File.Exists(LegacyDbPath))
        {
            LegacyStatusText = $"旧版库文件不存在：{LegacyDbPath}";
            return;
        }

        IsLegacyImporting = true;
        LegacyStatusText = "正在导入…";
        try
        {
            var progress = new Progress<LegacyImportProgress>(state =>
            {
                LegacyStatusText =
                    $"正在导入… {state.Processed}/{state.ScannedTotal}（新增 {state.Added}，跳过 {state.Skipped}）";
            });

            LegacyImportReport report = await _legacyImporter.ImportAsync(
                LegacyDbPath, progress);

            if (report.ErrorMessage is { } error)
            {
                LegacyStatusText = error;
                return;
            }

            LegacyStatusText = report.Cancelled
                ? $"已取消（新增 {report.AddedCount}，跳过 {report.SkippedCount}）"
                : $"导入完成：新增 {report.AddedCount} 张，跳过 {report.SkippedCount} 张，"
                  + $"标签 {report.TagCount} 条，收藏夹 {report.CollectionCount} 个";

            _notifications.Publish(
                NotificationKind.LegacyImportCompleted,
                report.Cancelled ? "旧版导入已取消" : "旧版库导入完成",
                LegacyStatusText);
            RefreshStatistics();
        }
        finally
        {
            IsLegacyImporting = false;
        }
    }

    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_logsDirectory);
        SystemNavigator.RevealFile(_logsDirectory);
    }

    private void RefreshStatistics()
    {
        int total = _imageQuery.TotalCount();
        int missing = _imageQuery.MissingCount();
        StatisticsText = $"图片 {total} 张，其中缺失 {missing} 张";
    }
}
