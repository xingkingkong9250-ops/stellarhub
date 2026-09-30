using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using Xunit;

namespace QL.World.Tests;

/// <summary>
/// M6-A: AccountService end-to-end through an in-memory NAS connector.
/// Covers create / authenticate / disable / reset / delete / duplicate-name
/// / cannot-delete-self / cannot-delete-last-admin.
/// </summary>
public class AccountServiceTests
{
    private readonly InMemoryNasConnector _nas = new();
    private readonly SharedStorageService _storage;
    private readonly AccountService _accounts;

    public AccountServiceTests()
    {
        _storage = new SharedStorageService(_nas);
        _accounts = new AccountService(_storage);
    }

    [Fact]
    public async Task InitializeAdmin_creates_admin_when_empty()
    {
        var admin = await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        Assert.Equal("admin", admin.Username);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.True(admin.IsActive);
    }

    [Fact]
    public async Task InitializeAdmin_throws_when_accounts_exist()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.InitializeAdminAsync("admin123", CancellationToken.None));
    }

    [Fact]
    public async Task CreateAccount_adds_user()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        var user = await _accounts.CreateAccountAsync("alice", "pass123", UserRole.User, "admin", CancellationToken.None);

        Assert.Equal("alice", user.Username);
        Assert.Equal(UserRole.User, user.Role);
        Assert.Equal("admin", user.CreatedBy);
    }

    [Fact]
    public async Task CreateAccount_rejects_duplicate_username_case_insensitive()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);
        await _accounts.CreateAccountAsync("Alice", "pass123", UserRole.User, "admin", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _accounts.CreateAccountAsync("alice", "pass456", UserRole.User, "admin", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("thisusernameiswaytoolong01234567890123456")]
    public async Task CreateAccount_rejects_invalid_username(string username)
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _accounts.CreateAccountAsync(username, "pass123", UserRole.User, "admin", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    public async Task CreateAccount_rejects_short_password(string password)
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _accounts.CreateAccountAsync("bob", password, UserRole.User, "admin", CancellationToken.None));
    }

    [Fact]
    public async Task Authenticate_returns_account_on_success()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        var account = await _accounts.AuthenticateAsync("admin", "admin123", CancellationToken.None);

        Assert.NotNull(account);
        Assert.Equal("admin", account!.Username);
    }

    [Fact]
    public async Task Authenticate_returns_null_on_wrong_password()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        var account = await _accounts.AuthenticateAsync("admin", "wrong", CancellationToken.None);

        Assert.Null(account);
    }

    [Fact]
    public async Task Authenticate_returns_null_on_unknown_user()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        var account = await _accounts.AuthenticateAsync("nobody", "admin123", CancellationToken.None);

        Assert.Null(account);
    }

    [Fact]
    public async Task Authenticate_returns_null_for_disabled_account()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);
        await _accounts.CreateAccountAsync("alice", "pass123", UserRole.User, "admin", CancellationToken.None);

        await _accounts.DisableAccountAsync("alice", CancellationToken.None);

        var account = await _accounts.AuthenticateAsync("alice", "pass123", CancellationToken.None);
        Assert.Null(account);
    }

    [Fact]
    public async Task EnableAccount_allows_login_again()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);
        await _accounts.CreateAccountAsync("alice", "pass123", UserRole.User, "admin", CancellationToken.None);
        await _accounts.DisableAccountAsync("alice", CancellationToken.None);

        await _accounts.EnableAccountAsync("alice", CancellationToken.None);

        var account = await _accounts.AuthenticateAsync("alice", "pass123", CancellationToken.None);
        Assert.NotNull(account);
    }

    [Fact]
    public async Task ResetPassword_changes_password()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);
        await _accounts.CreateAccountAsync("alice", "oldpass", UserRole.User, "admin", CancellationToken.None);

        await _accounts.ResetPasswordAsync("alice", "newpass", CancellationToken.None);

        Assert.Null(await _accounts.AuthenticateAsync("alice", "oldpass", CancellationToken.None));
        Assert.NotNull(await _accounts.AuthenticateAsync("alice", "newpass", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAccount_removes_user()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);
        await _accounts.CreateAccountAsync("alice", "pass123", UserRole.User, "admin", CancellationToken.None);

        await _accounts.DeleteAccountAsync("alice", "admin", CancellationToken.None);

        var all = await _accounts.ListAccountsAsync(CancellationToken.None);
        Assert.DoesNotContain(all, a => a.Username == "alice");
    }

    [Fact]
    public async Task DeleteAccount_cannot_delete_self()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.DeleteAccountAsync("admin", "admin", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAccount_cannot_delete_last_admin()
    {
        await _accounts.InitializeAdminAsync("admin123", CancellationToken.None);
        await _accounts.CreateAccountAsync("alice", "pass123", UserRole.Admin, "admin", CancellationToken.None);
        await _accounts.DisableAccountAsync("alice", CancellationToken.None);

        // admin is the only active admin now
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _accounts.DeleteAccountAsync("admin", "alice", CancellationToken.None));
    }
}
