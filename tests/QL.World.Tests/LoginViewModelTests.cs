using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>
/// M6-B: LoginViewModel tests. Uses a fake INasConnectorFactory that returns
/// an InMemoryNasConnector so we can test auth flows without a real WebDAV server.
/// </summary>
public sealed class LoginViewModelTests
{
    private sealed class FakeNasFactory : INasConnectorFactory
    {
        private readonly InMemoryNasConnector _connector = new();
        public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
            => _connector;
    }

    private static (LoginViewModel vm, AppSettingsService settings) CreateSut()
    {
        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(), $"stellarhub_login_{Guid.NewGuid():N}"));
        settings.SetNasConnections(new[]
        {
            new NasConnectionSpec
            {
                Id = 1,
                Name = "Test NAS",
                Host = "nas.test",
                Protocol = NasConnectionProtocol.WebDav,
                Username = "user",
            },
        });
        var vm = new LoginViewModel(settings, new FakeNasFactory());
        return (vm, settings);
    }

    [Fact]
    public async Task Login_with_no_connection_shows_error()
    {
        var (vm, _) = CreateSut();
        vm.SelectedConnection = null;
        vm.Username = "admin";

        await vm.LoginAsync("password");

        Assert.Contains("选择 NAS", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_with_empty_username_shows_error()
    {
        var (vm, _) = CreateSut();
        vm.Username = "";

        await vm.LoginAsync("password");

        Assert.Contains("用户名", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_with_wrong_credentials_shows_error()
    {
        var (vm, _) = CreateSut();
        vm.Username = "nobody";

        await vm.LoginAsync("wrong");

        Assert.Contains("错误", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_with_admin_credentials_succeeds()
    {
        var (vm, _) = CreateSut();
        // Bootstrap an admin account in the in-memory NAS first.
        await vm.InitializeAdminAsync("admin123");
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
        var (vm, _) = CreateSut();
        bool raised = false;
        vm.LocalModeRequested += () => raised = true;

        vm.LocalModeCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public async Task InitializeAdmin_creates_admin_account()
    {
        var (vm, _) = CreateSut();

        await vm.InitializeAdminAsync("admin123");

        // Should be able to log in with the bootstrapped admin.
        vm.Username = "admin";
        CurrentUser? captured = null;
        vm.LoginSucceeded += u => captured = u;

        await vm.LoginAsync("admin123");

        Assert.NotNull(captured);
    }
}
