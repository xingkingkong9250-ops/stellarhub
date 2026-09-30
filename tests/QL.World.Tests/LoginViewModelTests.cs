using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>
/// M6-B + v0.2.1: LoginViewModel tests. v0.2.1 起登录窗口不再让用户选
/// NAS——系统自动取主 NAS（IsPrimary=true 优先，无主取第一个），所以
/// 测试不再覆盖 SelectedConnection；改成测主 NAS 解析 + 预填用户名 +
/// CanLogin 联动。FakeNasFactory 接受外部 InMemoryNasConnector 实例，让
/// 测试可以预置 admin 账号。
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
    public void LocalModeCommand_raises_event()
    {
        var (vm, _, _) = CreateSut();
        bool raised = false;
        vm.LocalModeRequested += () => raised = true;

        vm.LocalModeCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void OpenNasSettingsCommand_raises_event()
    {
        // v0.2.1: 未登录场景下打开 NAS 设置入口的事件桥接
        var (vm, _, _) = CreateSut();
        bool raised = false;
        vm.NasSettingsRequested += () => raised = true;

        vm.OpenNasSettingsCommand.Execute(null);

        Assert.True(raised);
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
