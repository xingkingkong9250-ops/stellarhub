using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;

namespace QL.World.ViewModels;

/// <summary>
/// 登录窗口（W5）ViewModel。系统自动取主 NAS（IsPrimary=true 的连接，
/// 无主则取列表第一个）。NAS 配置入口收敛到窗口右上角齿轮（W5-6），
/// 由 App.xaml.cs 打开 W3 设置窗口。用户直接关窗即进入仅本地模式
/// （ShowDialog 返回非 true）。
///
/// v0.5.0 起支持「记住账户」（W5-4，持久化用户名）与「记录密码」
/// （W5-5，密码经 DPAPI CurrentUser 加密落 config.json），登录成功后
/// 按勾选状态写入/清除；启动时自动回填。
///
/// 启动时若检测到主 NAS 可达但 accounts.json 为空，App.xaml.cs 会自动
/// 创建预置管理员账号（XingKongKong）并把用户名预填到 <see cref="Username"/>。
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private const string NoNasHint = "尚未配置 NAS 连接，请点右上角齿轮在「NAS 连接」页签配置主 NAS。";

    private readonly AppSettingsService _settings;
    private readonly INasConnectorFactory _nasFactory;

    private string _username;
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private bool _hasNasConnection;
    private bool _rememberAccount;
    private bool _rememberPassword;

    public LoginViewModel(AppSettingsService settings, INasConnectorFactory nasFactory, string? presetUsername = null)
    {
        _settings = settings;
        _nasFactory = nasFactory;

        // 首次启动预置管理员优先；否则回填记住的用户名。
        _username = presetUsername ?? _settings.RememberedLoginUsername ?? string.Empty;
        _rememberAccount = !string.IsNullOrEmpty(_settings.RememberedLoginUsername);
        _rememberPassword = _settings.HasRememberedLoginPassword;

        // 初始 CanLogin 取决于是否有可用 NAS 连接
        _hasNasConnection = ResolvePrimarySpec() is not null;
        if (!_hasNasConnection)
            _errorMessage = NoNasHint;
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

    /// <summary>
    /// W5-4: 记住账户。取消勾选时连带取消「记录密码」——没有用户名的
    /// 密码记录没有意义。
    /// </summary>
    public bool RememberAccount
    {
        get => _rememberAccount;
        set
        {
            if (SetProperty(ref _rememberAccount, value) && !value && _rememberPassword)
                RememberPassword = false;
        }
    }

    /// <summary>
    /// W5-5: 记录密码。勾选时连带勾选「记住账户」。
    /// </summary>
    public bool RememberPassword
    {
        get => _rememberPassword;
        set
        {
            if (SetProperty(ref _rememberPassword, value) && value && !_rememberAccount)
                RememberAccount = true;
        }
    }

    /// <summary>登录按钮可用条件：已配置 NAS 且未在登录中。</summary>
    public bool CanLogin => _hasNasConnection && !IsBusy;

    /// <summary>Raised when authentication succeeds. Carries the new session context.</summary>
    public event Action<CurrentUser>? LoginSucceeded;

    /// <summary>Raised when the user clicks the corner gear (W5-6) to open settings.</summary>
    public event Action? SettingsRequested;

    public RelayCommand OpenSettingsCommand => new(() => SettingsRequested?.Invoke());

    /// <summary>
    /// View 启动时调用一次：若勾选了「记录密码」，取回明文供 PasswordBox
    /// 回填；未记录返回 null。明文只在 View 事件中短暂存在，不做绑定属性。
    /// </summary>
    public string? GetRememberedPasswordForPrefill()
        => _rememberPassword ? _settings.GetRememberedLoginPassword() : null;

    /// <summary>
    /// 主 NAS 解析：IsPrimary=true 的优先；无主则取列表第一个；列表空返回 null。
    /// </summary>
    private NasConnectionSpec? ResolvePrimarySpec()
        => _settings.NasConnections.FirstOrDefault(c => c.IsPrimary)
            ?? _settings.NasConnections.FirstOrDefault();

    /// <summary>
    /// v0.2.1: App.xaml.cs 在设置窗口关闭后调用——重读
    /// settings.NasConnections + 更新 CanLogin + 错误提示。让用户配好 NAS
    /// 后无需重启即可直接登录。
    /// </summary>
    public void RefreshNasState()
    {
        _hasNasConnection = ResolvePrimarySpec() is not null;
        if (!_hasNasConnection)
            ErrorMessage = NoNasHint;
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
            ErrorMessage = NoNasHint;
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

            PersistCredentials(Username.Trim(), password);
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

    /// <summary>
    /// W5-4/W5-5: 仅在登录成功后落盘。记住账户→存用户名；记录密码→DPAPI
    /// 加密存密码；未勾选则清除对应字段（取消账户同时清除密码）。
    /// </summary>
    private void PersistCredentials(string username, string password)
    {
        if (RememberAccount)
        {
            _settings.RememberedLoginUsername = username;
            _settings.SetRememberedLoginPassword(RememberPassword ? password : null);
        }
        else
        {
            _settings.SetRememberedLoginPassword(null);
            _settings.RememberedLoginUsername = null;
        }
    }
}
