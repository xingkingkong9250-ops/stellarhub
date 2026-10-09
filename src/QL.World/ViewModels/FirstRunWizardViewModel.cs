using System.IO;
using QL.World.Helpers;
using QL.World.Data;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// W7 首次启动向导 ViewModel：①选择软件托管图片库目录（同盘校验）；
/// 老用户（检测到旧 qlworld.db）额外选择 ②立即迁移老数据 / 跳过。
/// 纯逻辑可单测：文件夹选择器以 <see cref="Func{String}"/> 注入
/// （生产用 WPF OpenFolderDialog，测试传桩）；窗口关闭由
/// <see cref="RequestClose"/> 事件驱动。
///
/// 「跳过」是软跳过：置 settings.LegacyDataSkipped=true，画廊默认过滤
/// 未托管行（ImageQueryService），W3/A1 可随时开关放行。
/// </summary>
public sealed class FirstRunWizardViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly DatabaseService _database;
    private readonly string _dataRoot;
    private readonly Func<string?> _folderPicker;
    private readonly Action<string> _log;

    private string _selectedLibraryPath = string.Empty;
    private string _validationError = string.Empty;
    private bool _isMigrating;
    private string _migrationStatus = string.Empty;

    public FirstRunWizardViewModel(
        AppSettingsService settings,
        DatabaseService database,
        string dataRoot,
        bool hasLegacyData,
        Func<string?>? folderPicker = null,
        Action<string>? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        _dataRoot = dataRoot;
        HasLegacyData = hasLegacyData;
        _folderPicker = folderPicker ?? DefaultFolderPicker;
        _log = log ?? (_ => { });

        BrowseCommand = new RelayCommand(Browse);
        MigrateNowCommand = new RelayCommand(MigrateNow, () => CanProceed && !IsMigrating);
        SkipCommand = new RelayCommand(Skip, () => CanProceed && !IsMigrating);
        ConfirmCommand = new RelayCommand(Confirm, () => CanProceed && !IsMigrating);
    }

    /// <summary>老库存在时为 true（显示迁移/跳过第二步）；新安装只显示确认。</summary>
    public bool HasLegacyData { get; }

    /// <summary>true=立即迁移；false=跳过；null=新安装无老数据。关闭后由 App 读取。</summary>
    public bool? MigrationDecision { get; private set; }

    public string SelectedLibraryPath
    {
        get => _selectedLibraryPath;
        set
        {
            if (SetProperty(ref _selectedLibraryPath, value))
            {
                Revalidate();
                OnPropertyChanged(nameof(CanProceed));
            }
        }
    }

    public string ValidationError
    {
        get => _validationError;
        private set => SetProperty(ref _validationError, value);
    }

    public bool IsMigrating
    {
        get => _isMigrating;
        private set
        {
            if (SetProperty(ref _isMigrating, value))
                OnPropertyChanged(nameof(CanProceed));
        }
    }

    public string MigrationStatus
    {
        get => _migrationStatus;
        private set
        {
            if (SetProperty(ref _migrationStatus, value))
                OnPropertyChanged(nameof(HasMigrationStatus));
        }
    }

    public bool HasMigrationStatus => !string.IsNullOrEmpty(_migrationStatus);

    public bool HasValidationError => !string.IsNullOrEmpty(_validationError);

    public bool CanProceed =>
        !IsMigrating
        && !string.IsNullOrWhiteSpace(_selectedLibraryPath)
        && string.IsNullOrEmpty(_validationError);

    public RelayCommand BrowseCommand { get; }
    public RelayCommand MigrateNowCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand ConfirmCommand { get; }

    /// <summary>Raised when the wizard has finished; payload is the dialog result.</summary>
    public event Action<bool>? RequestClose;

    private void Browse()
    {
        string? picked = _folderPicker();
        if (!string.IsNullOrWhiteSpace(picked))
            SelectedLibraryPath = picked;
    }

    private void Revalidate()
    {
        if (string.IsNullOrWhiteSpace(_selectedLibraryPath))
        {
            ValidationError = string.Empty;
            return;
        }

        (bool ok, string? error) =
            LibraryRootResolver.ValidateSameVolume(_selectedLibraryPath, _dataRoot);
        ValidationError = ok ? string.Empty : (error ?? "路径无效。");
        OnPropertyChanged(nameof(HasValidationError));
        RaiseCommands();
    }

    private void MigrateNow()
    {
        if (!TryPersistRoot()) return;

        IsMigrating = true;
        RaiseCommands();
        try
        {
            MigrationStatus = "正在迁移老数据，请稍候……";
            var migrator = new LegacyDataMigrator(
                _database, _settings.LibraryRootPath!, log: _log);
            LegacyDataMigrator.Result result = migrator.Run(
                progress: new Progress<(int Processed, int Total, string Current)>(p =>
                {
                    MigrationStatus = $"正在迁移 {p.Processed}/{p.Total}：{Path.GetFileName(p.Current)}";
                }));

            MigrationStatus = $"迁移完成：成功 {result.Migrated}，缺失跳过 {result.SkippedMissing}，" +
                              $"NAS 跳过 {result.SkippedRemote}，失败 {result.Failed}。";
            MigrationDecision = true;
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            IsMigrating = false;
            MigrationStatus = $"迁移失败：{ex.Message}";
            RaiseCommands();
        }
    }

    private void Skip()
    {
        if (!TryPersistRoot()) return;
        // 软跳过：不清数据；画廊默认不显示未托管行，W3/A1 可放行。
        _settings.LegacyDataSkipped = true;
        MigrationDecision = false;
        RequestClose?.Invoke(true);
    }

    private void Confirm()
    {
        if (!TryPersistRoot()) return;
        MigrationDecision = null;
        RequestClose?.Invoke(true);
    }

    private bool TryPersistRoot()
    {
        if (!CanProceed) return false;
        try
        {
            _settings.ConfigureLibraryRoot(_selectedLibraryPath);
            return true;
        }
        catch (Exception ex)
        {
            ValidationError = $"无法保存库目录：{ex.Message}";
            OnPropertyChanged(nameof(HasValidationError));
            return false;
        }
    }

    private void RaiseCommands()
    {
        MigrateNowCommand.RaiseCanExecuteChanged();
        SkipCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
    }

    private static string? DefaultFolderPicker()
    {
        // Microsoft.Win32.OpenFolderDialog (WPF .NET 8+).
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择图片库目录（需与数据目录同一盘符）",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
