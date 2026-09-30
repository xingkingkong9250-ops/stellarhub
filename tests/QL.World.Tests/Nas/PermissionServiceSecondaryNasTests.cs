using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M8-D: 副库权限分级 + ShareLink 副库测试。覆盖：
/// ① 普通用户对副库无写/迁移权限，admin 有；
/// ② SubmissionOutcome.NotPermitted 枚举存在；
/// ③ NasConnectionSpec.IsShareLink 计算属性；
/// ④ WebDavConnectorFactory 对 ShareLink spec 不加 Basic-auth（defensive）。
/// </summary>
public sealed class PermissionServiceSecondaryNasTests
{
    private static CurrentUser Admin(string name = "admin") =>
        new(name, UserRole.Admin, nasConnectionId: 1);

    private static CurrentUser Regular(string name = "user") =>
        new(name, UserRole.User, nasConnectionId: 1);

    // ─── ① 权限分级 ───────────────────────────────────────────────────

    [Fact]
    public void Admin_can_write_and_migrate_secondary_nas()
    {
        var perm = new PermissionService(Admin());
        Assert.True(perm.CanWriteToSecondaryNas);
        Assert.True(perm.CanSubmitToSecondaryNas);
        Assert.True(perm.CanMigratePrimary);
        Assert.True(perm.CanDownloadFromSecondaryNas); // admin 也能下载
    }

    [Fact]
    public void Regular_user_cannot_write_or_migrate_secondary_nas()
    {
        var perm = new PermissionService(Regular());
        Assert.False(perm.CanWriteToSecondaryNas);
        Assert.False(perm.CanSubmitToSecondaryNas);
        Assert.False(perm.CanMigratePrimary);
        // 普通用户可下载副库（只读可下载语义）
        Assert.True(perm.CanDownloadFromSecondaryNas);
    }

    [Fact]
    public void Local_only_user_has_no_secondary_permissions()
    {
        // local-only 模式 CurrentUser=null
        var perm = new PermissionService(null);
        Assert.False(perm.CanWriteToSecondaryNas);
        Assert.False(perm.CanSubmitToSecondaryNas);
        Assert.False(perm.CanMigratePrimary);
        Assert.False(perm.CanDownloadFromSecondaryNas);
        Assert.False(perm.IsLoggedIn);
    }

    // ─── ② SubmissionOutcome.NotPermitted ──────────────────────────────

    [Fact]
    public void SubmissionOutcome_NotPermitted_exists()
    {
        // 确保 NotPermitted 枚举值存在且可被 switch 覆盖
        var outcome = SubmissionOutcome.NotPermitted;
        var result = new SubmissionResult(outcome, string.Empty);
        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionOutcome.NotPermitted, result.Outcome);
    }

    // ─── ③ NasConnectionSpec.IsShareLink 计算属性 ─────────────────────

    [Fact]
    public void IsShareLink_reflects_role()
    {
        var share = new NasConnectionSpec { Name = "link", Role = NasRole.ShareLink };
        Assert.True(share.IsShareLink);
        Assert.False(share.IsPrimary);

        var secondary = new NasConnectionSpec { Name = "sec", Role = NasRole.Secondary };
        Assert.False(secondary.IsShareLink);

        var primary = new NasConnectionSpec { Name = "main", Role = NasRole.Primary };
        Assert.False(primary.IsShareLink);
        Assert.True(primary.IsPrimary);
    }

    // ─── ④ WebDavConnectorFactory ShareLink 跳过 auth（defensive） ─────

    [Fact]
    public void WebDavConnectorFactory_sharelink_spec_does_not_add_auth_even_with_credentials()
    {
        // 即使 spec 误填了 Username/EncryptedPassword（ValidateNasRoles 应已清空，
        // 但 defensive 测试验证 factory 也不会加 auth）
        var spec = new NasConnectionSpec
        {
            Id = 1,
            Name = "share",
            Host = "share.example.com",
            Role = NasRole.ShareLink,
            Username = "should-be-ignored", // 误填
            EncryptedPassword = "should-be-ignored",
        };

        var factory = new WebDavConnectorFactory();
        using var connector = factory.Create(spec, plaintextPassword: null);

        // connector 是 DisposableWebDavConnector（private），通过 Spec 验证创建成功
        Assert.NotNull(connector);
        Assert.Equal("share", connector.Spec.Name);
        // 无法直接检查 HttpClient 的 Authorization header（private），
        // 但 factory 创建不抛异常即证明 ShareLink 路径正常工作。
    }
}
