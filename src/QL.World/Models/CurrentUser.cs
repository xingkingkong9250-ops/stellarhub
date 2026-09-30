using QL.World.Models;

namespace QL.World.Models;

/// <summary>
/// M6-B: context of the currently logged-in application user.
/// Null when the user chose "local-only mode" (no NAS account).
/// Set by App.xaml.cs after LoginWindow succeeds; consumed by
/// PermissionService and admin-only UI entry points.
/// </summary>
public sealed class CurrentUser
{
    public string Username { get; }
    public UserRole Role { get; }
    public int NasConnectionId { get; }

    public CurrentUser(string username, UserRole role, int nasConnectionId)
    {
        Username = username;
        Role = role;
        NasConnectionId = nasConnectionId;
    }

    public bool IsAdmin => Role == UserRole.Admin;
}
