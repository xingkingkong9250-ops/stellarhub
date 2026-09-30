using System.Windows;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;

namespace QL.World.ViewModels;

/// <summary>
/// M6-B: login screen logic. The user picks a NAS connection (whose shared
/// directory holds accounts.json), enters username + password, and we
/// authenticate through <see cref="AccountService"/>. On success the
/// <see cref="LoginSucceeded"/> event carries a <see cref="CurrentUser"/>
/// for App.xaml.cs to wire into the shell.
///
/// Two escape hatches: "Local only mode" skips NAS login (shared library
/// unavailable), and "Initialize admin" bootstraps the first admin account
/// when accounts.json does not yet exist on the NAS.
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly INasConnectorFactory _nasFactory;

    private NasConnectionSpec? _selectedConnection;
    private string _username = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isBusy;

    public LoginViewModel(AppSettingsService settings, INasConnectorFactory nasFactory)
    {
        _settings = settings;
        _nasFactory = nasFactory;

        NasConnections = _settings.NasConnections;
        SelectedConnection = NasConnections.FirstOrDefault();
    }

    public IReadOnlyList<NasConnectionSpec> NasConnections { get; }

    public NasConnectionSpec? SelectedConnection
    {
        get => _selectedConnection;
        set => SetProperty(ref _selectedConnection, value);
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
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Raised when authentication succeeds. Carries the new session context.</summary>
    public event Action<CurrentUser>? LoginSucceeded;

    /// <summary>Raised when the user chooses local-only mode (no NAS account).</summary>
    public event Action? LocalModeRequested;

    public async Task LoginAsync(string password)
    {
        if (SelectedConnection is null)
        {
            ErrorMessage = "请选择 NAS 连接";
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
            using var connector = _nasFactory.Create(SelectedConnection, plaintextPassword: null);
            var storage = new SharedStorageService(connector);
            var accounts = new AccountService(storage);

            var account = await accounts.AuthenticateAsync(Username, password, CancellationToken.None);
            if (account is null)
            {
                ErrorMessage = "用户名或密码错误，或账号已被禁用";
                return;
            }

            LoginSucceeded?.Invoke(new CurrentUser(account.Username, account.Role, SelectedConnection.Id));
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

    public RelayCommand LocalModeCommand => new(() => LocalModeRequested?.Invoke());

    /// <summary>
    /// Bootstraps the first admin account on the selected NAS. Used when
    /// accounts.json does not exist yet. Default password is "admin123"
    /// (the admin should change it immediately after first login).
    /// </summary>
    public async Task InitializeAdminAsync(string defaultPassword)
    {
        if (SelectedConnection is null)
        {
            ErrorMessage = "请选择 NAS 连接";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            using var connector = _nasFactory.Create(SelectedConnection, plaintextPassword: null);
            var storage = new SharedStorageService(connector);
            await storage.EnsureSharedStructureAsync(CancellationToken.None);
            var accounts = new AccountService(storage);
            await accounts.InitializeAdminAsync(defaultPassword, CancellationToken.None);
            ErrorMessage = "管理员账号已初始化（用户名 admin），请登录后修改密码";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"初始化失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
