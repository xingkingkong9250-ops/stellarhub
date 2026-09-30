using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services.Nas;

namespace QL.World.Services;

/// <summary>
/// M6-A: application account management. Accounts are stored in NAS-shared
/// accounts.json via <see cref="SharedStorageService"/>. All mutations follow
/// a read-modify-write cycle (last-write-wins; acceptable for small teams).
/// Passwords are PBKDF2-hashed — plaintext never touches the NAS.
/// </summary>
public sealed class AccountService
{
    private readonly SharedStorageService _storage;

    public AccountService(SharedStorageService storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>
    /// Creates the first admin account when accounts.json does not exist or
    /// is empty. Returns the created admin; throws if accounts already exist.
    /// </summary>
    public async Task<UserAccount> InitializeAdminAsync(string defaultPassword, CancellationToken ct)
    {
        var accounts = (await _storage.LoadAccountsAsync(ct).ConfigureAwait(false)).ToList();
        if (accounts.Count > 0)
            throw new InvalidOperationException("Accounts already initialized; cannot create bootstrap admin.");

        var (hash, salt) = PasswordHasher.Hash(defaultPassword);
        var admin = new UserAccount
        {
            Username = "admin",
            PasswordHash = hash,
            Salt = salt,
            Role = UserRole.Admin,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "system",
        };
        accounts.Add(admin);
        await _storage.SaveAccountsAsync(accounts, ct).ConfigureAwait(false);
        return admin;
    }

    /// <summary>
    /// Creates a new account. Username must be unique (case-insensitive).
    /// <paramref name="createdBy"/> is the username of the admin performing
    /// the creation.
    /// </summary>
    public async Task<UserAccount> CreateAccountAsync(
        string username,
        string password,
        UserRole role,
        string createdBy,
        CancellationToken ct)
    {
        ValidateUsername(username);
        ValidatePassword(password);

        var accounts = (await _storage.LoadAccountsAsync(ct).ConfigureAwait(false)).ToList();
        if (accounts.Any(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"用户名 '{username}' 已存在", nameof(username));

        var (hash, salt) = PasswordHasher.Hash(password);
        var account = new UserAccount
        {
            Username = username,
            PasswordHash = hash,
            Salt = salt,
            Role = role,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
        };
        accounts.Add(account);
        await _storage.SaveAccountsAsync(accounts, ct).ConfigureAwait(false);
        return account;
    }

    /// <summary>
    /// Authenticates a user. Returns the account on success; null when the
    /// username does not exist, the password is wrong, or the account is disabled.
    /// </summary>
    public async Task<UserAccount?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return null;

        var accounts = await _storage.LoadAccountsAsync(ct).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a =>
            a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        if (account is null || !account.IsActive)
            return null;

        return PasswordHasher.Verify(password, account.PasswordHash, account.Salt)
            ? account
            : null;
    }

    public async Task<IReadOnlyList<UserAccount>> ListAccountsAsync(CancellationToken ct)
    {
        return await _storage.LoadAccountsAsync(ct).ConfigureAwait(false);
    }

    public async Task DisableAccountAsync(string username, CancellationToken ct)
    {
        await SetStatusAsync(username, "disabled", ct).ConfigureAwait(false);
    }

    public async Task EnableAccountAsync(string username, CancellationToken ct)
    {
        await SetStatusAsync(username, "active", ct).ConfigureAwait(false);
    }

    public async Task ResetPasswordAsync(string username, string newPassword, CancellationToken ct)
    {
        ValidatePassword(newPassword);

        var accounts = (await _storage.LoadAccountsAsync(ct).ConfigureAwait(false)).ToList();
        int idx = accounts.FindIndex(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ArgumentException($"用户 '{username}' 不存在", nameof(username));

        var (hash, salt) = PasswordHasher.Hash(newPassword);
        accounts[idx] = accounts[idx] with { PasswordHash = hash, Salt = salt };
        await _storage.SaveAccountsAsync(accounts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an account. Cannot delete the calling user (<paramref name="actorUsername"/>)
    /// and cannot delete the last remaining admin.
    /// </summary>
    public async Task DeleteAccountAsync(string username, string actorUsername, CancellationToken ct)
    {
        if (username.Equals(actorUsername, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能删除当前登录的账号");

        var accounts = (await _storage.LoadAccountsAsync(ct).ConfigureAwait(false)).ToList();
        int idx = accounts.FindIndex(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ArgumentException($"用户 '{username}' 不存在", nameof(username));

        var target = accounts[idx];
        if (target.Role == UserRole.Admin &&
            accounts.Count(a => a.Role == UserRole.Admin && a.IsActive) <= 1)
        {
            throw new InvalidOperationException("不能删除最后一个活跃管理员账号");
        }

        accounts.RemoveAt(idx);
        await _storage.SaveAccountsAsync(accounts, ct).ConfigureAwait(false);
    }

    // ─── internals ──────────────────────────────────────────────────────

    private async Task SetStatusAsync(string username, string status, CancellationToken ct)
    {
        var accounts = (await _storage.LoadAccountsAsync(ct).ConfigureAwait(false)).ToList();
        int idx = accounts.FindIndex(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ArgumentException($"用户 '{username}' 不存在", nameof(username));

        accounts[idx] = accounts[idx] with { Status = status };
        await _storage.SaveAccountsAsync(accounts, ct).ConfigureAwait(false);
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("用户名不能为空", nameof(username));
        if (username.Length < 2)
            throw new ArgumentException("用户名至少 2 个字符", nameof(username));
        if (username.Length > 32)
            throw new ArgumentException("用户名最多 32 个字符", nameof(username));
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("密码不能为空", nameof(password));
        if (password.Length < 6)
            throw new ArgumentException("密码至少 6 个字符", nameof(password));
    }
}
