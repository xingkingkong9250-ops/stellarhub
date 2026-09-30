using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Nas;
using QL.World.Services.Pbr;

namespace QL.World.ViewModels;

/// <summary>
/// W3 settings window. A1 general (theme / integrity), A2 thumbnails,
/// A3 AI (cloud config + local model download), A4 watcher directories,
/// A5 legacy import & diagnostics, A6 PBR grouping rules, A7 NAS connections.
/// Every change saves immediately through AppSettingsService.
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
    private readonly INasConnectorFactory _nasConnectorFactory;
    private readonly AccountService? _accounts;
    private readonly CurrentUser? _currentUser;
    /// <summary>
    /// M8-E: 登录后的多 NAS 注册中心。null 表示未登录或 local-only 模式，
    /// 此时「设为主 NAS 并迁移」按钮不可用（仅保留旧「仅切标记」入口）。
    /// </summary>
    private readonly NasConnectionRegistry? _nasRegistry;
    private bool _isMigrating;
    private string _migrationStatusText = string.Empty;

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
        string logsDirectory,
        PbrGroupingService pbrGrouping,
        INasConnectorFactory nasConnectorFactory,
        AccountService? accounts,
        CurrentUser? currentUser,
        NasConnectionRegistry? nasRegistry = null)
    {
        _settings = settings;
        _thumbnails = thumbnails;
        _imageQuery = imageQuery;
        _watcher = watcher;
        _legacyImporter = legacyImporter;
        _modelDownload = modelDownload;
        _notifications = notifications;
        _logsDirectory = logsDirectory;
        _nasConnectorFactory = nasConnectorFactory;
        _accounts = accounts;
        _currentUser = currentUser;
        _nasRegistry = nasRegistry;
        PbrRules = new PbrRulesViewModel(settings, pbrGrouping);

        _selectedTheme = settings.Theme;
        _legacyDbPath = LegacyPythonImporter.DefaultLegacyDbPath;

        foreach (string directory in settings.WatcherDirectories)
            WatcherDirectories.Add(directory);

        foreach (var c in settings.NasConnections)
            NasConnections.Add(c);

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

        AddNasConnectionCommand = new RelayCommand(AddNasConnection);
        AddShareLinkCommand = new RelayCommand(AddShareLink);
        DeleteNasConnectionCommand = new RelayCommand(
            DeleteNasConnection, () => SelectedNasConnection is not null);
        SaveNasConnectionCommand = new RelayCommand(
            SaveNasConnection, () => SelectedNasConnection is not null);
        SetPrimaryCommand = new RelayCommand(
            SetPrimary, () => SelectedNasConnection is not null && !SelectedNasConnection.IsPrimary && !IsMigrating);
        /// <summary>
        /// M8-E: 「设为主 NAS 并迁移数据」命令。仅管理员 + 已登录 + 选中副库时可用。
        /// 实际迁移由 <see cref="PrimaryMigrationService.MigrateAsync"/> 执行；
        /// 命令体先弹确认框（含源主库文件数预览），用户确认后才执行。
        /// </summary>
        SetPrimaryWithMigrationCommand = new AsyncRelayCommand(
            SetPrimaryWithMigrationAsync,
            () => _nasRegistry is not null
                && SelectedNasConnection is not null
                && !SelectedNasConnection.IsPrimary
                && !SelectedNasConnection.IsShareLink
                && !IsMigrating);
        TestNasConnectionCommand = new AsyncRelayCommand(
            TestNasConnectionAsync,
            () => SelectedNasConnection is not null && !IsTestingNasConnection);
        /// <summary>M8-E: 「搬运到此处」引导命令（管理员 + 选中副库时可用）。</summary>
        TransferToHereCommand = new RelayCommand(
            ShowTransferToHereGuidance,
            () => IsAdmin
                && SelectedNasConnection is not null
                && !SelectedNasConnection.IsPrimary
                && !SelectedNasConnection.IsShareLink);

        // M6-C: user management (admin only)
        AddUserCommand = new AsyncRelayCommand(AddUserAsync, () => IsUserManagementEnabled);
        ToggleUserStatusCommand = new AsyncRelayCommand(ToggleUserStatusAsync, () => SelectedAccount is not null);
        ResetPasswordCommand = new AsyncRelayCommand(ResetPasswordAsync, () => SelectedAccount is not null);
        DeleteUserCommand = new AsyncRelayCommand(DeleteUserAsync, () => SelectedAccount is not null);
        RefreshAccountsCommand = new AsyncRelayCommand(RefreshAccountsAsync, () => IsUserManagementEnabled);

        RefreshLocalModelState();
        RefreshStatistics();
        _ = LoadAccountsAsync();
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

    /// <summary>File name / size / license / source line shown under the buttons.
    /// Covers both the vision and text models in the M3-E2 bundle.</summary>
    public string LocalModelInfoText =>
        $"{ModelManifest.ModelFileName} · {ModelManifest.ModelSizeBytes / (1024 * 1024)} MB + "
        + $"{ModelManifest.TextModelFileName} · {ModelManifest.TextModelSizeBytes / (1024 * 1024)} MB · "
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
                else if (p.Phase == ModelDownloadPhase.Verifying && !string.IsNullOrEmpty(p.Message))
                    LocalStatusText = p.Message;
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

    // ─── A6 贴图组规则 ──────────────────────────────────────────────────────

    public PbrRulesViewModel PbrRules { get; }

    // ─── A7 NAS 连接 ──────────────────────────────────────────────────────

    public ObservableCollection<NasConnectionSpec> NasConnections { get; } = [];

    public IReadOnlyList<NasConnectionProtocol> NasProtocolOptions { get; } = [NasConnectionProtocol.WebDav, NasConnectionProtocol.Smb];

    private NasConnectionSpec? _selectedNasConnection;
    public NasConnectionSpec? SelectedNasConnection
    {
        get => _selectedNasConnection;
        set
        {
            if (SetProperty(ref _selectedNasConnection, value))
                RaiseNasProtocolDerived();
        }
    }

    // 派生属性：协议联动 UI 显隐 + 提示文案。N3/N5 各自按协议呈现。
    public bool IsWebDavSelected => SelectedNasConnection?.Protocol == NasConnectionProtocol.WebDav;
    public bool IsSmbSelected => SelectedNasConnection?.Protocol == NasConnectionProtocol.Smb;
    public bool IsTlsApplicable => IsWebDavSelected || IsSmbSelected;
    public bool IsTrustSelfSignedApplicable => IsWebDavSelected;

    public string NasPortHintText =>
        SelectedNasConnection?.Protocol switch
        {
            NasConnectionProtocol.Smb => "端口（留空=默认 445）",
            _ => "端口（留空=默认 443/80）",
        };

    public string NasRootPathHint =>
        SelectedNasConnection?.Protocol switch
        {
            NasConnectionProtocol.Smb => "根路径（如 StellarHubShared/）",
            _ => "根路径（如 /photos/）",
        };

    public string NasTlsLabelText =>
        SelectedNasConnection?.Protocol == NasConnectionProtocol.Smb
            ? "SMB3 加密协商"
            : "使用 HTTPS/TLS（推荐）";

    /// <summary>
    /// View 在协议 ComboBox SelectionChanged 时调用。显式传入新协议（从
    /// ComboBox.SelectedItem 读取，不依赖双向绑定 push 时序），写回 spec
    /// 并检测 Port 是否为前一协议默认值（443/445）或 null——是则填新协议
    /// 默认；非默认值保留。spec 是 POCO 无 INPC，借由 View 事件桥接避免
    /// 改造持久化 schema。
    /// </summary>
    public void OnNasProtocolChanged(NasConnectionProtocol newProtocol)
    {
        if (SelectedNasConnection is not { } spec) return;
        spec.Protocol = newProtocol;
        int? port = spec.Port;
        int next = newProtocol == NasConnectionProtocol.Smb ? 445 : 443;
        int prev = newProtocol == NasConnectionProtocol.Smb ? 443 : 445;
        if (port is null || port == prev)
            spec.Port = next;
        RaiseNasProtocolDerived();
    }

    private void RaiseNasProtocolDerived()
    {
        OnPropertyChanged(nameof(IsWebDavSelected));
        OnPropertyChanged(nameof(IsSmbSelected));
        OnPropertyChanged(nameof(IsTlsApplicable));
        OnPropertyChanged(nameof(IsTrustSelfSignedApplicable));
        OnPropertyChanged(nameof(NasPortHintText));
        OnPropertyChanged(nameof(NasRootPathHint));
        OnPropertyChanged(nameof(NasTlsLabelText));
        // M8-E: 选中项变化时刷新「设为主 NAS」类命令的可用态
        SetPrimaryCommand.RaiseCanExecuteChanged();
        SetPrimaryWithMigrationCommand.RaiseCanExecuteChanged();
        TransferToHereCommand.RaiseCanExecuteChanged();
    }

    private string _nasStatusText = string.Empty;
    public string NasStatusText
    {
        get => _nasStatusText;
        private set => SetProperty(ref _nasStatusText, value);
    }

    private bool _isTestingNasConnection;
    public bool IsTestingNasConnection
    {
        get => _isTestingNasConnection;
        private set
        {
            if (SetProperty(ref _isTestingNasConnection, value))
                TestNasConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand AddNasConnectionCommand { get; }
    /// <summary>M8-D: 添加分享链接（ShareLink 副库，匿名访问，无凭据）。</summary>
    public RelayCommand AddShareLinkCommand { get; }
    public RelayCommand DeleteNasConnectionCommand { get; }
    public RelayCommand SaveNasConnectionCommand { get; }
    public RelayCommand SetPrimaryCommand { get; }
    /// <summary>M8-E: 「设为主 NAS 并迁移数据」命令。</summary>
    public AsyncRelayCommand SetPrimaryWithMigrationCommand { get; }
    public AsyncRelayCommand TestNasConnectionCommand { get; }

    /// <summary>
    /// M8-E: 主 NAS 迁移进行中。True 时禁用「仅切标记」「设为主 NAS 并迁移」
    /// 命令，UI 显示进度文案；False 时恢复命令可用态。
    /// </summary>
    public bool IsMigrating
    {
        get => _isMigrating;
        private set
        {
            if (SetProperty(ref _isMigrating, value))
            {
                SetPrimaryCommand.RaiseCanExecuteChanged();
                SetPrimaryWithMigrationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>M8-E: 迁移过程文案（"复制文件 12/100 — originals/ab/..."）。UI 顶部显示。</summary>
    public string MigrationStatusText
    {
        get => _migrationStatusText;
        private set => SetProperty(ref _migrationStatusText, value);
    }

    /// <summary>
    /// v0.2.1: A7 NAS 页签管理员门禁。未登录（首次配置场景）或当前用户为
    /// 管理员时可见；普通用户隐藏。与 <see cref="IsUserManagementEnabled"/>
    /// 的「仅管理员」不同——本属性允许 null currentUser 以支持首次启动
    /// 配置 NAS。
    /// </summary>
    public bool IsNasManagementEnabled => _currentUser is null || _currentUser.IsAdmin;

    /// <summary>
    /// M8-E: 当前用户是否为管理员。仅登录后 + admin 角色才返回 true。
    /// 用于「设为主 NAS 并迁移」「搬运到此处」等管理员专属按钮的可见性绑定。
    /// </summary>
    public bool IsAdmin => _currentUser?.IsAdmin == true;

    /// <summary>
    /// M8-E: 是否在共享画廊显示数据来源徽章（nas://{id}）。默认 false。
    /// 持久化于 config.json，绑定 A7 的 CheckBox。
    /// </summary>
    public bool ShowSourceBadge
    {
        get => _settings.ShowSourceBadge;
        set => _settings.ShowSourceBadge = value;
    }

    /// <summary>
    /// M8-E: 「搬运到此处」引导命令。当前为引导提示——实际数据搬运流程
    /// 在共享画廊右键菜单（管理员选中 N 张图片 → 搬运到副库 → 选目标）。
    /// 数据搬运服务 <see cref="DataTransferService"/> 已就绪可被调用。
    /// </summary>
    public RelayCommand TransferToHereCommand { get; }

    private void AddNasConnection()
    {
        var spec = new NasConnectionSpec
        {
            Name = "新连接",
            Protocol = NasConnectionProtocol.WebDav,
            RootPath = "/",
            UseTls = true,
            Role = NasRole.Secondary, // M8-D: 新增连接默认副库
        };
        _settings.AddNasConnection(spec);
        RefreshNasConnections();
        SelectedNasConnection = NasConnections.FirstOrDefault(c => c.Id == spec.Id);
    }

    /// <summary>
    /// M8-D: 添加分享链接（ShareLink 副库）。匿名访问，无 Username/Password，
    /// 仅需 Host + RootPath。A7 UI 隐藏密码框（Role==ShareLink 时）。
    /// </summary>
    private void AddShareLink()
    {
        var spec = new NasConnectionSpec
        {
            Name = "新分享链接",
            Protocol = NasConnectionProtocol.WebDav,
            RootPath = "/",
            UseTls = true,
            Role = NasRole.ShareLink,
        };
        _settings.AddNasConnection(spec);
        RefreshNasConnections();
        SelectedNasConnection = NasConnections.FirstOrDefault(c => c.Id == spec.Id);
    }

    private void DeleteNasConnection()
    {
        if (SelectedNasConnection is null) return;
        int id = SelectedNasConnection.Id;
        _settings.DeleteNasConnection(id);
        RefreshNasConnections();
        SelectedNasConnection = NasConnections.FirstOrDefault();
    }

    private void SaveNasConnection()
    {
        if (SelectedNasConnection is null) return;
        _settings.UpdateNasConnection(SelectedNasConnection);
        NasStatusText = "已保存连接。";
        RefreshNasConnections();
    }

    /// <summary>
    /// v0.2.1: 把选中 NAS 标记为主 NAS（IsPrimary=true），其他连接全部
    /// 设为 false（仅允许一个主 NAS）。逐个调 UpdateNasConnection 持久化。
    /// 刷新列表 + 重选当前项以触发 UI「主」徽标更新。
    /// </summary>
    private void SetPrimary()
    {
        if (SelectedNasConnection is null) return;
        int primaryId = SelectedNasConnection.Id;
        foreach (var c in NasConnections)
        {
            bool newPrimary = c.Id == primaryId;
            if (c.IsPrimary != newPrimary)
            {
                c.IsPrimary = newPrimary;
                _settings.UpdateNasConnection(c);
            }
        }
        NasStatusText = "已设为主 NAS。";
        RefreshNasConnections();
        SelectedNasConnection = NasConnections.FirstOrDefault(c => c.Id == primaryId);
        SetPrimaryCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// M8-E: 「设为主 NAS 并迁移数据」——弹确认框（含源主库文件数预览）后调
    /// <see cref="PrimaryMigrationService.MigrateAsync"/>。失败时旧主库 Role
    /// 不切换、共享库仍可读，已复制文件保留（幂等，下次重试跳过）。
    /// 成功后刷新列表 + 重选当前项以触发 UI「主」徽标更新。
    /// </summary>
    private async Task SetPrimaryWithMigrationAsync()
    {
        if (SelectedNasConnection is null || _nasRegistry is null) return;
        if (SelectedNasConnection.IsPrimary || SelectedNasConnection.IsShareLink) return;

        var sourceEntry = _nasRegistry.GetPrimary();
        if (sourceEntry is null)
        {
            MigrationStatusText = "无当前主 NAS，无法迁移。";
            return;
        }
        int newPrimaryId = SelectedNasConnection.Id;

        // 预览源主库文件数 + 总字节数（递归 originals/ + thumbnails/ + pending/）
        MigrationStatusText = "正在统计源主库文件…";
        int fileCount;
        long totalBytes;
        try
        {
            (fileCount, totalBytes) = await CountSharedFilesAsync(
                sourceEntry.Value.Connector, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MigrationStatusText = $"统计失败：{ex.Message}";
            return;
        }

        // 确认框
        var result = MessageBox.Show(
            $"将迁移约 {fileCount} 个文件（约 {totalBytes / 1024.0 / 1024.0:F1} MB）到「{SelectedNasConnection.Name}」。\n" +
            "迁移期间共享库不可用；失败可重试（幂等，已复制文件跳过）。\n" +
            "是否继续？",
            "设为主 NAS 并迁移数据",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK)
        {
            MigrationStatusText = string.Empty;
            return;
        }

        // 执行迁移
        IsMigrating = true;
        MigrationStatusText = "迁移中…";
        var progress = new Progress<MigrationProgress>(p =>
            MigrationStatusText = $"[{p.Phase}] {p.DoneFiles}/{p.TotalFiles} — {p.CurrentPath}");
        try
        {
            var migrationService = new PrimaryMigrationService(_nasRegistry, _settings);
            await migrationService.MigrateAsync(newPrimaryId, progress, CancellationToken.None);
            MigrationStatusText = "迁移完成，已切换主 NAS。";
            NasStatusText = "已设为主 NAS（含迁移）。";
            RefreshNasConnections();
            SelectedNasConnection = NasConnections.FirstOrDefault(c => c.Id == newPrimaryId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MigrationStatusText = $"迁移失败：{ex.Message}（旧主库仍可用，可重试）";
        }
        finally
        {
            IsMigrating = false;
        }
    }

    /// <summary>
    /// 递归统计源 NAS 共享目录的文件总数 + 总字节数。覆盖 originals/ + thumbnails/ + pending/。
    /// 用于迁移确认框的文件数预览（不需要精确大小，PROPFIND 一次即可拿到 Size）。
    /// </summary>
    private static async Task<(int count, long bytes)> CountSharedFilesAsync(
        INasConnector conn, CancellationToken ct)
    {
        var originals = await CountDirAsync(conn,
            $"{SharedPaths.Root}/{SharedPaths.OriginalsDir}", ct).ConfigureAwait(false);
        var thumbnails = await CountDirAsync(conn,
            $"{SharedPaths.Root}/{SharedPaths.ThumbnailsDir}", ct).ConfigureAwait(false);
        var pending = await CountDirAsync(conn,
            $"{SharedPaths.Root}/{SharedPaths.PendingDir}", ct).ConfigureAwait(false);
        return (originals.count + thumbnails.count + pending.count,
            originals.bytes + thumbnails.bytes + pending.bytes);
    }

    private static async Task<(int count, long bytes)> CountDirAsync(
        INasConnector conn, string dir, CancellationToken ct)
    {
        IReadOnlyList<NasFileInfo> entries;
        try
        {
            entries = await conn.ListFilesAsync(dir, ct).ConfigureAwait(false);
        }
        catch (NasConnectionException ex) when (ex.Kind == NasConnectionKind.NotFound)
        {
            return (0, 0);
        }
        int count = 0;
        long bytes = 0;
        foreach (var e in entries)
        {
            if (e.IsDirectory)
            {
                var sub = await CountDirAsync(conn, $"{dir}/{e.Name}", ct).ConfigureAwait(false);
                count += sub.count;
                bytes += sub.bytes;
            }
            else
            {
                count++;
                bytes += e.Size;
            }
        }
        return (count, bytes);
    }

    /// <summary>
    /// M8-E: 「搬运到此处」按钮引导文案。当前数据搬运流程的入口在共享画廊
    /// 右键菜单（管理员选中 N 张图片 → 搬运到副库 → 选目标副库），
    /// A7 设置面板的按钮作为提示入口避免管理员找不到流程。
    /// </summary>
    private void ShowTransferToHereGuidance()
    {
        if (SelectedNasConnection is null) return;
        MessageBox.Show(
            $"数据搬运流程：\n" +
            "1. 在主窗口「共享画廊」选中 N 张图片\n" +
            "2. 右键 → 搬运到副库...\n" +
            $"3. 选择目标副库「{SelectedNasConnection.Name}」\n\n" +
            "搬运将逐张 download→upload 至目标副库，" +
            "目标 library.json AddOrUpdate + 源 library.json Remove。" +
            "源 NAS 文件在上传成功后删除（失败不删源）。",
            $"搬运到「{SelectedNasConnection.Name}」",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task TestNasConnectionAsync()
    {
        if (SelectedNasConnection is null || IsTestingNasConnection) return;
        IsTestingNasConnection = true;
        NasStatusText = "正在测试连接…";
        INasConnector? connector = null;
        try
        {
            // Persist current edits first so the encrypted password is fresh
            // (the user may have just typed a new password into the PasswordBox).
            _settings.UpdateNasConnection(SelectedNasConnection);
            connector = _nasConnectorFactory.Create(SelectedNasConnection, plaintextPassword: null);
            bool ok = await connector.TestConnectionAsync(CancellationToken.None);
            NasStatusText = ok ? "连接成功。" : "连接失败（请检查地址/凭据/网络）。";
        }
        catch (NasConnectionException ex)
        {
            NasStatusText = $"连接失败：{ex.Kind}（{ex.Message}）";
        }
        catch (Exception ex)
        {
            NasStatusText = $"连接失败：{ex.Message}";
        }
        finally
        {
            IsTestingNasConnection = false;
            (connector as IDisposable)?.Dispose();
        }
    }

    /// <summary>Called from the View code-behind on PasswordBox.PasswordChanged.</summary>
    public void SetNasPasswordInput(int id, string plaintext)
    {
        _settings.SetNasPassword(id, plaintext);
    }

    /// <summary>
    /// Re-syncs the ObservableCollection from settings (after add/update/delete)
    /// and re-selects the previously selected id when still present.
    /// </summary>
    private void RefreshNasConnections()
    {
        int? selectedId = SelectedNasConnection?.Id;
        NasConnections.Clear();
        foreach (var c in _settings.NasConnections)
            NasConnections.Add(c);
        if (selectedId.HasValue)
        {
            SelectedNasConnection = NasConnections.FirstOrDefault(c => c.Id == selectedId.Value);
        }
    }

    // ─── Events ─────────────────────────────────────────────────────────────

    /// <summary>App wires this to SetTheme + persist (setter already persists).</summary>
    public event Action<string>? ThemeChanged;

    /// <summary>Window closes on invoke.</summary>
    public event Action? RequestClose;

    // ─── Actions ────────────────────────────────────────────────────────────

    private void RegenerateAllThumbnails()
    {
        var items = _imageQuery.QueryGallery(new LibraryFilter());
        // M5-E: ThumbnailService.Request now takes IImageSource. Settings
        // regen builds LocalImageSource for local rows and skips remote rows
        // (their thumbnails re-generate lazily as the gallery scrolls and
        // GalleryThumbnailProvider.Attach builds a RemoteImageSource with the
        // cached connector). Building a per-row connector here would leak.
        int skipped = 0;
        foreach (GalleryListItem item in items)
        {
            if (item.Source == ImageSource.Remote)
            {
                skipped++;
                continue;
            }
            _thumbnails.Regenerate(item.FileHash, new LocalImageSource(item.FilePath));
        }
        StatusBarText = skipped > 0
            ? $"已重新排队 {items.Count - skipped} 张缩略图（跳过 {skipped} 张远程）"
            : $"已重新排队 {items.Count} 张缩略图";
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

    // ─── M6-C 用户管理（仅管理员）──────────────────────────────────────────

    /// <summary>True when the current user is an admin and NAS account service is available.</summary>
    public bool IsUserManagementEnabled => _accounts is not null && _currentUser?.IsAdmin == true;

    public ObservableCollection<UserAccount> Accounts { get; } = new();

    private UserAccount? _selectedAccount;
    public UserAccount? SelectedAccount
    {
        get => _selectedAccount;
        set => SetProperty(ref _selectedAccount, value);
    }

    private string _newUserName = string.Empty;
    public string NewUserName
    {
        get => _newUserName;
        set => SetProperty(ref _newUserName, value);
    }

    private string _newUserPassword = string.Empty;
    public string NewUserPassword
    {
        get => _newUserPassword;
        set => SetProperty(ref _newUserPassword, value);
    }

    private bool _newUserIsAdmin;
    public bool NewUserIsAdmin
    {
        get => _newUserIsAdmin;
        set => SetProperty(ref _newUserIsAdmin, value);
    }

    private string _userManagementStatus = string.Empty;
    public string UserManagementStatus
    {
        get => _userManagementStatus;
        set => SetProperty(ref _userManagementStatus, value);
    }

    public AsyncRelayCommand AddUserCommand { get; }
    public AsyncRelayCommand ToggleUserStatusCommand { get; }
    public AsyncRelayCommand ResetPasswordCommand { get; }
    public AsyncRelayCommand DeleteUserCommand { get; }
    public AsyncRelayCommand RefreshAccountsCommand { get; }

    private async Task LoadAccountsAsync()
    {
        if (!IsUserManagementEnabled) return;
        try
        {
            var accounts = await _accounts!.ListAccountsAsync(CancellationToken.None);
            Accounts.Clear();
            foreach (var a in accounts)
                Accounts.Add(a);
        }
        catch (Exception ex)
        {
            UserManagementStatus = $"加载账号失败：{ex.Message}";
        }
    }

    private async Task RefreshAccountsAsync() => await LoadAccountsAsync();

    private async Task AddUserAsync()
    {
        if (_accounts is null || _currentUser is null) return;
        if (string.IsNullOrWhiteSpace(NewUserName) || string.IsNullOrEmpty(NewUserPassword))
        {
            UserManagementStatus = "请输入用户名和密码";
            return;
        }
        try
        {
            var role = NewUserIsAdmin ? UserRole.Admin : UserRole.User;
            await _accounts.CreateAccountAsync(NewUserName, NewUserPassword, role, _currentUser.Username, CancellationToken.None);
            NewUserName = string.Empty;
            NewUserPassword = string.Empty;
            NewUserIsAdmin = false;
            UserManagementStatus = "用户已创建";
            await LoadAccountsAsync();
        }
        catch (Exception ex)
        {
            UserManagementStatus = $"创建失败：{ex.Message}";
        }
    }

    private async Task ToggleUserStatusAsync()
    {
        if (_accounts is null || SelectedAccount is null) return;
        try
        {
            if (SelectedAccount.IsActive)
                await _accounts.DisableAccountAsync(SelectedAccount.Username, CancellationToken.None);
            else
                await _accounts.EnableAccountAsync(SelectedAccount.Username, CancellationToken.None);
            UserManagementStatus = "状态已更新";
            await LoadAccountsAsync();
        }
        catch (Exception ex)
        {
            UserManagementStatus = $"操作失败：{ex.Message}";
        }
    }

    private async Task ResetPasswordAsync()
    {
        if (_accounts is null || SelectedAccount is null) return;
        // Reset to a known default; admin should tell the user to change it.
        const string defaultPassword = "StellarHub123";
        try
        {
            await _accounts.ResetPasswordAsync(SelectedAccount.Username, defaultPassword, CancellationToken.None);
            UserManagementStatus = $"密码已重置为 {defaultPassword}，请通知用户登录后修改";
        }
        catch (Exception ex)
        {
            UserManagementStatus = $"重置失败：{ex.Message}";
        }
    }

    private async Task DeleteUserAsync()
    {
        if (_accounts is null || SelectedAccount is null || _currentUser is null) return;
        try
        {
            await _accounts.DeleteAccountAsync(SelectedAccount.Username, _currentUser.Username, CancellationToken.None);
            SelectedAccount = null;
            UserManagementStatus = "用户已删除";
            await LoadAccountsAsync();
        }
        catch (Exception ex)
        {
            UserManagementStatus = $"删除失败：{ex.Message}";
        }
    }
}
