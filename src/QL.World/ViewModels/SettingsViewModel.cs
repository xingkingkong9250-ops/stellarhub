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
/// A3 AI (cloud config + local model download), A4 watcher directories,
/// A5 legacy import & diagnostics. Every change saves immediately through
/// AppSettingsService.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly ThumbnailService _thumbnails;
    private readonly ImageQueryService _imageQuery;
    private readonly FileWatcherService _watcher;
    private readonly LegacyPythonImporter _legacyImporter;
    private readonly ModelDownloadService _modelDownload;
    private readonly NotificationService _notifications;
    private readonly string _logsDirectory;

    private string _selectedTheme;
    private string? _selectedWatcherDirectory;
    private string _statisticsText = string.Empty;
    private string _legacyStatusText = string.Empty;
    private string _statusBarText = string.Empty;
    private string _legacyDbPath;
    private string _localStatusText = string.Empty;
    private LocalModelState _localModelState = LocalModelState.NotDownloaded;
    private bool _isLegacyImporting;
    private bool _isTestingConnection;
    private bool _isDownloading;
    private double _downloadPercent;
    private CancellationTokenSource? _downloadCts;

    public SettingsViewModel(
        AppSettingsService settings,
        ThumbnailService thumbnails,
        ImageQueryService imageQuery,
        FileWatcherService watcher,
        LegacyPythonImporter legacyImporter,
        ModelDownloadService modelDownload,
        NotificationService notifications,
        string logsDirectory)
    {
        _settings = settings;
        _thumbnails = thumbnails;
        _imageQuery = imageQuery;
        _watcher = watcher;
        _legacyImporter = legacyImporter;
        _modelDownload = modelDownload;
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
        DownloadModelCommand = new AsyncRelayCommand(
            DownloadModelAsync,
            () => !_isDownloading && _localModelState != LocalModelState.Ready);
        CancelDownloadCommand = new RelayCommand(
            CancelDownload, () => _isDownloading);
        SelectModelFileCommand = new AsyncRelayCommand(
            SelectModelFileAsync,
            () => !_isDownloading);
        DeleteModelCommand = new RelayCommand(
            DeleteModel,
            () => !_isDownloading && _localModelState != LocalModelState.NotDownloaded);

        RefreshLocalModelState();
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

    // ─── A3 本地模型 ────────────────────────────────────────────────────────

    /// <summary>Human-readable download/import state of the local model file.</summary>
    public string LocalModelStateText => _localModelState switch
    {
        LocalModelState.Ready => "模型已就绪",
        LocalModelState.Corrupted => "模型文件不完整，请删除后重新下载或手动导入",
        _ => "尚未下载",
    };

    /// <summary>File name / size / license / source line shown under the buttons.</summary>
    public string LocalModelInfoText =>
        $"{ModelManifest.ModelFileName} · {ModelManifest.ModelSizeBytes / (1024 * 1024)} MB · "
        + $"许可证 {ModelManifest.License} · 来源 huggingface.co（失败自动切换 hf-mirror.com 镜像）";

    public string LocalStatusText
    {
        get => _localStatusText;
        private set => SetProperty(ref _localStatusText, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            DownloadModelCommand.RaiseCanExecuteChanged();
            CancelDownloadCommand.RaiseCanExecuteChanged();
            SelectModelFileCommand.RaiseCanExecuteChanged();
            DeleteModelCommand.RaiseCanExecuteChanged();
        }
    }

    public double DownloadPercent
    {
        get => _downloadPercent;
        private set => SetProperty(ref _downloadPercent, value);
    }

    public AsyncRelayCommand DownloadModelCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public AsyncRelayCommand SelectModelFileCommand { get; }
    public RelayCommand DeleteModelCommand { get; }

    private async Task DownloadModelAsync()
    {
        if (IsDownloading) return;
        if (_localModelState == LocalModelState.Ready) return;

        _downloadCts = new CancellationTokenSource();
        DownloadPercent = 0;
        IsDownloading = true;
        LocalStatusText = "正在下载…";
        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (p.Phase == ModelDownloadPhase.Downloading)
                    DownloadPercent = p.Percent;
            });
            await _modelDownload.DownloadAsync(progress, _downloadCts.Token);
            LocalStatusText = "模型下载完成，校验通过。";
            _notifications.Publish(NotificationKind.LocalModelDownloadCompleted,
                "本地模型", "模型下载完成，本地 AI 分析已可用。");
        }
        catch (OperationCanceledException)
        {
            LocalStatusText = "已取消下载（已下载部分保留，下次自动续传）。";
        }
        catch (Exception ex)
        {
            LocalStatusText = $"下载失败：{ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            RefreshLocalModelState();
        }
    }

    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    private async Task SelectModelFileAsync()
    {
        if (IsDownloading) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择本地模型文件",
            Filter = "ONNX 模型 (*.onnx)|*.onnx|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        IsDownloading = true; // reuse the busy state so every button revalidates
        LocalStatusText = "正在导入并校验…";
        try
        {
            await _modelDownload.ImportManualAsync(dialog.FileName, CancellationToken.None);
            LocalStatusText = "模型导入完成，校验通过。";
            _notifications.Publish(NotificationKind.LocalModelDownloadCompleted,
                "本地模型", "手动导入的模型校验通过。");
        }
        catch (Exception ex)
        {
            LocalStatusText = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            RefreshLocalModelState();
        }
    }

    private void DeleteModel()
    {
        try
        {
            _modelDownload.DeleteModel();
            LocalStatusText = "已删除本地模型。";
        }
        catch (Exception ex)
        {
            LocalStatusText = $"删除失败：{ex.Message}";
        }
        RefreshLocalModelState();
    }

    /// <summary>
    /// Re-checks the model file on disk (stat + full SHA-256). Synchronous on
    /// purpose: deterministic for tests; production cost is ~1 s for 350 MB.
    /// </summary>
    private void RefreshLocalModelState()
    {
        _localModelState = _modelDownload.CheckLocalState();
        OnPropertyChanged(nameof(LocalModelStateText));
        DownloadModelCommand.RaiseCanExecuteChanged();
        DeleteModelCommand.RaiseCanExecuteChanged();
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
