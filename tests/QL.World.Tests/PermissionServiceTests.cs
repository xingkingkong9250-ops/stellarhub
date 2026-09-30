using QL.World.Models;
using QL.World.Services;

namespace QL.World.Tests;

/// <summary>M6-F: PermissionService tests.</summary>
public sealed class PermissionServiceTests
{
    [Fact]
    public void LocalOnlyMode_has_no_permissions()
    {
        var p = new PermissionService(null);
        Assert.False(p.IsLoggedIn);
        Assert.False(p.IsAdmin);
        Assert.False(p.CanManageUsers);
        Assert.False(p.CanApproveSubmissions);
        Assert.False(p.CanSubmitToLibrary);
        Assert.False(p.CanViewSharedLibrary);
    }

    [Fact]
    public void RegularUser_has_submit_and_view_only()
    {
        var user = new CurrentUser("alice", UserRole.User, 1);
        var p = new PermissionService(user);
        Assert.True(p.IsLoggedIn);
        Assert.False(p.IsAdmin);
        Assert.True(p.CanSubmitToLibrary);
        Assert.True(p.CanViewSharedLibrary);
        Assert.False(p.CanManageUsers);
        Assert.False(p.CanApproveSubmissions);
    }

    [Fact]
    public void Admin_has_all_permissions()
    {
        var admin = new CurrentUser("admin", UserRole.Admin, 1);
        var p = new PermissionService(admin);
        Assert.True(p.IsAdmin);
        Assert.True(p.CanManageUsers);
        Assert.True(p.CanApproveSubmissions);
        Assert.True(p.CanSubmitToLibrary);
        Assert.True(p.CanViewSharedLibrary);
    }

    [Fact]
    public void CanManageSubmission_allows_admin_and_submitter()
    {
        var admin = new CurrentUser("admin", UserRole.Admin, 1);
        var alice = new CurrentUser("alice", UserRole.User, 1);
        var bob = new CurrentUser("bob", UserRole.User, 1);

        Assert.True(new PermissionService(admin).CanManageSubmission("alice"));
        Assert.True(new PermissionService(alice).CanManageSubmission("alice"));
        Assert.False(new PermissionService(bob).CanManageSubmission("alice"));
    }
}
