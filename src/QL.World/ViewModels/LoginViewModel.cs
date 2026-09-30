using System.Windows;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;

namespace QL.World.ViewModels;

/// <summary>
/// v0.2.1 起：登录窗口不再让用户选 NAS——系统自动取主 NAS（IsPrimary=true
/// 的连接，无主则取列表第一个）。NAS 配置入口移到 W3/A7 设置界面，由
/// 管理员维护。登录窗口新增「打开 NAS 设置」入口，便于首次启动未配 NAS
/// 时直接进入 A7 页签配置。
///
/// 启动时若检测到主 NAS 可达但 accounts.json 为空，App.xaml.cs 会自动
/// 创建预置管理员账号（XingKongKong）并把用户名预填到 <see cref="Username"/>。
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly INasConnectorFactory _nasFactory;

    private string _username = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private bool _hasNasConnection;

    public LoginViewModel(AppSettingsService settings, INasConnectorFactory nasFactory, string? presetUsername = null)
    {
        _settings = settings;
        _nasFactory = nasFactory;
        _username = presetUsername ?? string.Empty;
        // 初始 CanLogin 取决于是否有可用 NAS 连接
        _hasNasConnection = ResolvePrimarySpec() is not null;
        if (!_hasNasConnection)
            _errorMessage = "尚未配置 NAS 连接，请点「打开 NAS 设置」配置并标记主 NAS 后重启。";
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanLogin));
        }
    }

    /// <summary>登录按钮可用条件：已配置 NAS 且未在登录中。</summary>
    public bool CanLogin => _hasNasConnection && !IsBusy;

    /// <summary>Raised when authentication succeeds. Carries the new session context.</summary>
    public event Action<CurrentUser>? LoginSucceeded;

    /// <summary>Raised when the user chooses local-only mode (no NAS account).</summary>
    public event Action? LocalModeRequested;

    /// <summary>Raised when the user clicks「打开 NAS 设置」(未登录也可开 A7 页签)。</summary>
    public event Action? NasSettingsRequested;

    public RelayCommand LocalModeCommand => new(() => LocalModeRequested?.Invoke());
    public RelayCommand OpenNasSettingsCommand => new(() => NasSettingsRequested?.Invoke());

    /// <summary>
    /// 主 NAS 解析：IsPrimary=true 的优先；无主则取列表第一个；列表空返回 null。
    /// </summary>
    private NasConnectionSpec? ResolvePrimarySpec()
        => _settings.NasConnections.FirstOrDefault(c => c.IsPrimary)
            ?? _settings.NasConnections.FirstOrDefault();

    /// <summary>
    /// v0.2.1: App.xaml.cs 在「打开 NAS 设置」窗口关闭后调用——重读
    /// settings.NasConnections + 更新 CanLogin + 错误提示。让用户配好 NAS
    /// 后无需重启即可直接登录。
    /// </summary>
    public void RefreshNasState()
    {
        _hasNasConnection = ResolvePrimarySpec() is not null;
        if (!_hasNasConnection)
            ErrorMessage = "尚未配置 NAS 连接，请点「打开 NAS 设置」配置并标记主 NAS 后重启。";
        else if (string.IsNullOrEmpty(ErrorMessage) == false
                 && ErrorMessage.StartsWith("尚未配置 NAS", StringComparison.Ordinal))
            ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(CanLogin));
    }

    public async Task LoginAsync(string password)
    {
        var spec = ResolvePrimarySpec();
        if (spec is null)
        {
            ErrorMessage = "尚未配置 NAS 连接，请点「打开 NAS 设置」配置。";
            return;
        }
        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "请输入用户名";
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            ErrorMessage = "请输入密码";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            using var connector = _nasFactory.Create(spec, plaintextPassword: null);
            var storage = new SharedStorageService(connector);
            var accounts = new AccountService(storage);

            var account = await accounts.AuthenticateAsync(Username, password, CancellationToken.None);
            if (account is null)
            {
                ErrorMessage = "用户名或密码错误，或账号已被禁用";
                return;
            }

            LoginSucceeded?.Invoke(new CurrentUser(account.Username, account.Role, spec.Id));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"登录失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
