using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>
/// M6-B + v0.2.1 + v0.5.0: LoginViewModel tests. v0.2.1 起登录窗口不再让
/// 用户选 NAS——系统自动取主 NAS（IsPrimary=true 优先，无主取第一个），
/// 测试覆盖主 NAS 解析 + 预填用户名 + CanLogin 联动。v0.5.0 起增加
/// W5-4/W5-5 记住账户/记录密码（DPAPI）回填、勾选级联与登录成功落盘/
/// 清除，以及角落齿轮设置事件。FakeNasFactory 接受外部 InMemoryNasConnector
/// 实例，让测试可以预置 admin 账号。
/// </summary>
public sealed class LoginViewModelTests
{
    private sealed class FakeNasFactory : INasConnectorFactory
    {
        private readonly InMemoryNasConnector _connector;
        public FakeNasFactory(InMemoryNasConnector connector) => _connector = connector;
        public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword) => _connector;
    }

    private static (LoginViewModel vm, AppSettingsService settings, InMemoryNasConnector connector) CreateSut(bool withPrimary = true)
    {
        var connector = new InMemoryNasConnector();
        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(), $"stellarhub_login_{Guid.NewGuid():N}"));
        if (withPrimary)
        {
            settings.SetNasConnections(new[]
            {
                new NasConnectionSpec
                {
                    Id = 1,
                    Name = "Test NAS",
                    Host = "nas.test",
                    Protocol = NasConnectionProtocol.WebDav,
                    Username = "user",
                    IsPrimary = true,
                },
            });
        }
        var vm = new LoginViewModel(settings, new FakeNasFactory(connector));
        return (vm, settings, connector);
    }

    [Fact]
    public void Ctor_without_nas_disables_login_and_hints_user()
    {
        var (vm, _, _) = CreateSut(withPrimary: false);

        Assert.False(vm.CanLogin);
        Assert.Contains("尚未配置 NAS", vm.ErrorMessage);
    }

    [Fact]
    public void Ctor_with_primary_nas_enables_login()
    {
        var (vm, _, _) = CreateSut(withPrimary: true);

        Assert.True(vm.CanLogin);
        Assert.DoesNotContain("尚未配置 NAS", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_with_empty_username_shows_error()
    {
        var (vm, _, _) = CreateSut();
        vm.Username = "";

        await vm.LoginAsync("password");

        Assert.Contains("用户名", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_with_wrong_credentials_shows_error()
    {
        var (vm, _, _) = CreateSut();
        vm.Username = "nobody";

        await vm.LoginAsync("wrong");

        Assert.Contains("错误", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_with_admin_credentials_succeeds()
    {
        var (vm, _, connector) = CreateSut();
        // 预置 admin 账号到 InMemory NAS（替代旧 vm.InitializeAdminAsync）
        var storage = new SharedStorageService(connector);
        var accounts = new AccountService(storage);
        await accounts.InitializeAdminAsync("admin", "admin123", CancellationToken.None);
        vm.Username = "admin";
        vm.ErrorMessage = string.Empty;

        CurrentUser? captured = null;
        vm.LoginSucceeded += u => captured = u;

        await vm.LoginAsync("admin123");

        Assert.NotNull(captured);
        Assert.Equal("admin", captured!.Username);
        Assert.Equal(UserRole.Admin, captured.Role);
    }

    [Fact]
    public void OpenSettingsCommand_raises_event()
    {
        // v0.5.0: 角落齿轮（W5-6）打开设置窗口的事件桥接
        var (vm, _, _) = CreateSut();
        bool raised = false;
        vm.SettingsRequested += () => raised = true;

        vm.OpenSettingsCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void Ctor_prefills_remembered_username_and_flags()
    {
        // v0.5.0 W5-4/W5-5: 模拟上一次登录勾选了记住账户 + 记录密码
        var connector = new InMemoryNasConnector();
        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(), $"stellarhub_login_{Guid.NewGuid():N}"));
        settings.SetNasConnections(new[]
        {
            new NasConnectionSpec
            {
                Id = 1, Name = "Test NAS", Host = "nas.test",
                Protocol = NasConnectionProtocol.WebDav, IsPrimary = true,
            },
        });
        settings.RememberedLoginUsername = "stored-user";
        settings.SetRememberedLoginPassword("stored-pass");

        var vm = new LoginViewModel(settings, new FakeNasFactory(connector));

        Assert.Equal("stored-user", vm.Username);
        Assert.True(vm.RememberAccount);
        Assert.True(vm.RememberPassword);
        Assert.Equal("stored-pass", vm.GetRememberedPasswordForPrefill());
    }

    [Fact]
    public void Ctor_without_remembered_credentials_leaves_boxes_clean()
    {
        var (vm, _, _) = CreateSut();

        Assert.Equal(string.Empty, vm.Username);
        Assert.False(vm.RememberAccount);
        Assert.False(vm.RememberPassword);
        Assert.Null(vm.GetRememberedPasswordForPrefill());
    }

    [Fact]
    public void Checking_remember_password_forces_remember_account()
    {
        var (vm, _, _) = CreateSut();

        vm.RememberPassword = true;

        Assert.True(vm.RememberAccount);
    }

    [Fact]
    public void Unchecking_remember_account_clears_remember_password()
    {
        var (vm, _, _) = CreateSut();
        vm.RememberPassword = true;
        Assert.True(vm.RememberAccount);

        vm.RememberAccount = false;

        Assert.False(vm.RememberPassword);
    }

    [Fact]
    public async Task Successful_login_persists_credentials_when_both_checked()
    {
        var (vm, settings, connector) = CreateSut();
        var storage = new SharedStorageService(connector);
        var accounts = new AccountService(storage);
        await accounts.InitializeAdminAsync("admin", "admin123", CancellationToken.None);
        vm.Username = "admin";
        vm.RememberAccount = true;
        vm.RememberPassword = true;

        await vm.LoginAsync("admin123");

        Assert.Equal("admin", settings.RememberedLoginUsername);
        Assert.True(settings.HasRememberedLoginPassword);
        Assert.Equal("admin123", settings.GetRememberedLoginPassword());

        // 模拟重启：新 ViewModel 从同一 settings 回填
        var reopened = new LoginViewModel(settings, new FakeNasFactory(connector));
        Assert.Equal("admin", reopened.Username);
        Assert.True(reopened.RememberAccount);
        Assert.True(reopened.RememberPassword);
        Assert.Equal("admin123", reopened.GetRememberedPasswordForPrefill());
    }

    [Fact]
    public async Task Successful_login_persists_username_only_when_only_account_checked()
    {
        var (vm, settings, connector) = CreateSut();
        var storage = new SharedStorageService(connector);
        var accounts = new AccountService(storage);
        await accounts.InitializeAdminAsync("admin", "admin123", CancellationToken.None);
        vm.Username = "admin";
        vm.RememberAccount = true;
        Assert.False(vm.RememberPassword);

        await vm.LoginAsync("admin123");

        Assert.Equal("admin", settings.RememberedLoginUsername);
        Assert.False(settings.HasRememberedLoginPassword);
    }

    [Fact]
    public async Task Successful_login_without_remember_clears_previously_stored_credentials()
    {
        var (vm, settings, connector) = CreateSut();
        // 模拟历史上记住过凭据，用户本次取消勾选
        settings.RememberedLoginUsername = "old-user";
        settings.SetRememberedLoginPassword("old-pass");
        vm = new LoginViewModel(settings, new FakeNasFactory(connector));
        Assert.True(vm.RememberAccount);
        vm.RememberAccount = false;

        var storage = new SharedStorageService(connector);
        var accounts = new AccountService(storage);
        await accounts.InitializeAdminAsync("admin", "admin123", CancellationToken.None);
        vm.Username = "admin";
        await vm.LoginAsync("admin123");

        Assert.Null(settings.RememberedLoginUsername);
        Assert.False(settings.HasRememberedLoginPassword);
    }

    [Fact]
    public async Task Failed_login_does_not_persist_credentials()
    {
        var (vm, settings, _) = CreateSut();
        vm.Username = "nobody";
        vm.RememberAccount = true;
        vm.RememberPassword = true;

        await vm.LoginAsync("wrong");

        Assert.Null(settings.RememberedLoginUsername);
        Assert.False(settings.HasRememberedLoginPassword);
    }

    [Fact]
    public async Task IsBusy_toggles_CanLogin()
    {
        var (vm, _, connector) = CreateSut();
        var storage = new SharedStorageService(connector);
        var accounts = new AccountService(storage);
        await accounts.InitializeAdminAsync("admin", "admin123", CancellationToken.None);
        vm.Username = "admin";

        // 模拟登录中：IsBusy=true 应让 CanLogin=false
        vm.IsBusy = true;
        Assert.False(vm.CanLogin);

        vm.IsBusy = false;
        Assert.True(vm.CanLogin);
    }

    [Fact]
    public void RefreshNasState_picks_up_newly_configured_primary()
    {
        // v0.2.1: 用户从「打开 NAS 设置」配好 NAS 后，登录窗口无需重启即可登录
        var (vm, settings, _) = CreateSut(withPrimary: false);
        Assert.False(vm.CanLogin);

        settings.SetNasConnections(new[]
        {
            new NasConnectionSpec
            {
                Id = 1, Name = "New NAS", Host = "nas.new",
                Protocol = NasConnectionProtocol.Smb, IsPrimary = true,
            },
        });
        vm.RefreshNasState();

        Assert.True(vm.CanLogin);
    }
}
