using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// M6-F: central permission checks. Evaluates whether the current
/// <see cref="CurrentUser"/> (or null in local-only mode) may perform an
/// action. Callers gate UI visibility / command CanExecute on these.
/// </summary>
public sealed class PermissionService
{
    private readonly CurrentUser? _currentUser;

    public PermissionService(CurrentUser? currentUser)
    {
        _currentUser = currentUser;
    }

    /// <summary>True when someone is logged in with a NAS account.</summary>
    public bool IsLoggedIn => _currentUser is not null;

    /// <summary>True when the logged-in user is an admin.</summary>
    public bool IsAdmin => _currentUser?.IsAdmin == true;

    /// <summary>The logged-in username, or null in local-only mode.</summary>
    public string? Username => _currentUser?.Username;

    /// <summary>Admins and the original submitter can manage a submission.</summary>
    public bool CanManageSubmission(string submittedBy)
        => IsAdmin || (Username is not null && Username == submittedBy);

    /// <summary>Only admins can approve/reject submissions.</summary>
    public bool CanApproveSubmissions => IsAdmin;

    /// <summary>Only admins can manage user accounts.</summary>
    public bool CanManageUsers => IsAdmin;

    /// <summary>Any logged-in user can submit to the shared library.</summary>
    public bool CanSubmitToLibrary => IsLoggedIn;

    /// <summary>Any logged-in user can view the shared library.</summary>
    public bool CanViewSharedLibrary => IsLoggedIn;

    // ─── M8 副库权限分级 ─────────────────────────────────────────────────

    /// <summary>
    /// M8-B: 管理员可对副库（Secondary/ShareLink）执行写操作（上传/删除/搬运）。
    /// 普通用户对副库只读可下载。主库写权限仍由 <see cref="CanSubmitToLibrary"/>
    /// 决定（IsLoggedIn，行为不变）。
    /// </summary>
    public bool CanWriteToSecondaryNas => IsAdmin;

    /// <summary>
    /// M8-B: 任意登录用户可从副库下载数据（扩充存储的语义即"可读可下载"）。
    /// 主库下载同样允许（<see cref="CanViewSharedLibrary"/> 已覆盖）。
    /// </summary>
    public bool CanDownloadFromSecondaryNas => IsLoggedIn;

    // ─── M8-D 副库权限分级 ─────────────────────────────────────────────────

    /// <summary>
    /// M8-D: 仅管理员可发起主 NAS 迁移（切换主库 + 自主迁移数据）。
    /// 普通用户无此权限。迁移是高影响操作，必须 admin 角色。
    /// </summary>
    public bool CanMigratePrimary => IsAdmin;

    /// <summary>
    /// M8-D: 仅管理员可向副库提交新数据（写权限）。普通用户对副库只读可下载。
    /// 主库提交权限仍由 <see cref="CanSubmitToLibrary"/>（IsLoggedIn）决定，行为不变。
    /// </summary>
    public bool CanSubmitToSecondaryNas => IsAdmin;
}
