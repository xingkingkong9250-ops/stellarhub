using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Ai;
using QL.World.Services.Nas;
using QL.World.Services.Pbr;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>
/// M6-C: user management tab in SettingsViewModel. Tests admin-only
/// account CRUD through an in-memory NAS + AccountService.
/// </summary>
public sealed class UserManagementTests
{
    private static (SettingsViewModel vm, AccountService accounts) CreateSut()
    {
        var connector = new InMemoryNasConnector();
        var storage = new SharedStorageService(connector);
        var accounts = new AccountService(storage);
        // Bootstrap an admin account.
        accounts.InitializeAdminAsync("admin", "admin123", CancellationToken.None).GetAwaiter().GetResult();
        var admin = accounts.AuthenticateAsync("admin", "admin123", CancellationToken.None).GetAwaiter().GetResult()!;

        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(), $"stellarhub_um_{Guid.NewGuid():N}"));
        // Build a minimal SettingsViewModel via reflection is overkill; instead
        // we test the account service directly through the VM's exposed surface
        // by constructing with a real AccountService.
        var vm = BuildSettingsVm(settings, accounts, new CurrentUser(admin.Username, admin.Role, 1));
        return (vm, accounts);
    }

    private static SettingsViewModel BuildSettingsVm(
        AppSettingsService settings, AccountService accounts, CurrentUser currentUser)
    {
        // Minimal construction: most services are null because user management
        // only touches _accounts and _currentUser. The constructor still
        // requires them; we pass stubs where needed.
        var lib = new TestLibrary();
        var imageQuery = new ImageQueryService(lib.Db);
        var inspector = new ImageInspector();
        var import = new ImportService(lib.Db, inspector);
        var notifications = new NotificationService();
        var watcher = new FileWatcherService(settings, import, imageQuery, notifications);

        return new SettingsViewModel(
            settings,
            new ThumbnailService(settings.DataRoot, settings),
            imageQuery,
            watcher,
            new LegacyPythonImporter(lib.Db),
            new ModelDownloadService(settings.DataRoot),
            notifications,
            Path.Combine(settings.DataRoot, "logs"),
            new PbrGroupingService(lib.Db, settings),
            new StubNasConnectorFactory(),
            accounts,
            currentUser);
    }

    [Fact]
    public void UserManagement_enabled_for_admin()
    {
        var (vm, _) = CreateSut();
        Assert.True(vm.IsUserManagementEnabled);
    }

    [Fact]
    public async Task LoadAccounts_lists_bootstrapped_admin()
    {
        var (vm, _) = CreateSut();

        await vm.RefreshAccountsCommand.ExecuteAsync();

        Assert.Contains(vm.Accounts, a => a.Username == "admin");
    }

    [Fact]
    public async Task AddUser_creates_account()
    {
        var (vm, accounts) = CreateSut();
        vm.NewUserName = "alice";
        vm.NewUserPassword = "alice123";
        vm.NewUserIsAdmin = false;

        await vm.AddUserCommand.ExecuteAsync();

        var all = await accounts.ListAccountsAsync(CancellationToken.None);
        Assert.Contains(all, a => a.Username == "alice" && a.Role == UserRole.User);
    }

    [Fact]
    public async Task DeleteUser_removes_account()
    {
        var (vm, accounts) = CreateSut();
        vm.NewUserName = "bob";
        vm.NewUserPassword = "bob1234";
        await vm.AddUserCommand.ExecuteAsync();

        var bob = vm.Accounts.First(a => a.Username == "bob");
        vm.SelectedAccount = bob;
        await vm.DeleteUserCommand.ExecuteAsync();

        var all = await accounts.ListAccountsAsync(CancellationToken.None);
        Assert.DoesNotContain(all, a => a.Username == "bob");
    }

    [Fact]
    public async Task ToggleUserStatus_disables_then_enables()
    {
        var (vm, _) = CreateSut();
        vm.NewUserName = "carol";
        vm.NewUserPassword = "carol123";
        await vm.AddUserCommand.ExecuteAsync();

        var carol = vm.Accounts.First(a => a.Username == "carol");
        vm.SelectedAccount = carol;

        await vm.ToggleUserStatusCommand.ExecuteAsync();
        Assert.DoesNotContain(vm.Accounts, a => a.Username == "carol" && a.IsActive);

        var disabled = vm.Accounts.First(a => a.Username == "carol");
        vm.SelectedAccount = disabled;
        await vm.ToggleUserStatusCommand.ExecuteAsync();
        Assert.Contains(vm.Accounts, a => a.Username == "carol" && a.IsActive);
    }
}
